using System.IO.Compression;
using CasuMp.SteamSession;
using LiteNetLib.Utils;
using SteamKit2;
using Polly.Contrib.WaitAndRetry;
using SteamKit2.Internal;

namespace CasuMpGateway;

public sealed class SteamLobby
{
    private const uint AppId = 4576510;

    public enum LobbyState
    {
        Disabled,    // 세션 파일 없음 / 초기화 안 됨 - 로비 불가
        Connecting,  // CM 연결 시도 중
        LoggingIn,   // 로그온 시도/대기 중
        WaitAuth,    // 로그온 성공, 오케스트레이터 AUTH_INFO 대기
        Creating,    // CreateLobby 발신 후 콜백 대기
        Created,     // 로비 생성 완료
        Failed,      // 로그온/생성 실패 (백오프 후 재시도)
    }

    private static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] RetryDelays = Backoff.DecorrelatedJitterBackoffV2(RetryMin, 10)
        .Select(delay => delay > RetryMax ? RetryMax : delay)
        .ToArray();
    private static readonly TimeSpan AuthWaitWarnInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SafetyRecycleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CreateFailRetry = TimeSpan.FromSeconds(10);

    private const string KeyLobbyName = "COOP_MOD_LOBBYNAME";
    private const string KeyVersion = "COOP_MOD_VERSION";
    private const string KeyGamemode = "COOP_MOD_GAMEMODE";
    private const string KeyCurrentLayer = "COOP_MOD_CURRENTLAYER";
    private const string KeyAvgMood = "COOP_MOD_AVGMOOD";
    private const string KeyDepth = "COOP_MOD_DEPTH";
    private const string KeyLivingCount = "COOP_MOD_LIVINGCOUNT";
    private const string KeyPlrCount = "COOP_MOD_PLRCOUNT";
    private const string KeyHasPassword = "COOP_MOD_HASPASSWORD";
    private const string KeyIsDedicated = "COOP_MOD_ISDEDICATED";
    private const string KeyLocked = "COOP_MOD_LOCKED";
    private const string KeyRunSettings = "COOP_MOD_RUNSETTINGS";
    private const string KeyDescription = "COOP_MOD_DESCRIPTION";
    private const string KeyLanguage = "COOP_MOD_LANGUAGE";
    private const string KeyMods = "COOP_MOD_MODS";

    private const string KeyExtraData = "COOP_MOD_EXTRADATA";

    private readonly string _sessionPath;
    private readonly string _versionTag;
    private readonly GatewayCore _core;

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamMatchmaking? _matchmaking;
    private bool _lobbyCreated;
    private bool _lobbyCreationPending;
    private DateTime _lobbyRetryAfterUtc;
    private bool _loggedOn;

    private LobbyState _state = LobbyState.Disabled;
    private SteamID? _lobbyId;
    private int _retryBackoff = 0;             // 0,1,2,... 지수 백오프 단계 (RetryMin*2^step, RetryMax 캡)
    private int _loginFailures;                // 연속 로그인 실패 횟수 (token 만료 의심 판정)
    private DateTime _lastAuthWaitWarnAtUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.UtcNow;
    private bool _manualRecycle;

    private DateTime _lastMetadataRefresh = DateTime.MinValue;
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(8);

    private int _livingCount;
    private int _happinessSum;
    private ulong[] _steamIds = Array.Empty<ulong>();
    private byte[]? _rulesBytes;
    private readonly string _motd;

    private const int MaxDescLength = 200;
    private const int MaxDescNewlines = 3;

    private const int MaxServerNameLength = 32;

    public bool Initialized { get; private set; }

    public SteamLobby(string sessionPath, string versionTag, GatewayCore core, string motd)
    {
        _sessionPath = sessionPath;
        _versionTag = versionTag;
        _core = core;
        _motd = motd ?? "";
    }

    public void UpdateDynamicMetadata(LobbyMetadataPayload payload)
    {
        _livingCount = payload.LivingCount;
        _happinessSum = payload.HappinessSum;
        _steamIds = payload.SteamIds ?? Array.Empty<ulong>();
        if (!string.IsNullOrEmpty(payload.RulesBase64))
        {
            try
            {
                _rulesBytes = Convert.FromBase64String(payload.RulesBase64);
            }
            catch (FormatException)
            {
                Logger.Info("LOBBY_METADATA RulesBase64 파싱 실패.");
            }
        }
        RefreshDynamicMetadata();
    }

    public void Start()
    {
        if (!File.Exists(_sessionPath))
        {
            Logger.Info($"세션 파일이 없습니다: {_sessionPath} (Steam 로비 비활성).");
            _state = LobbyState.Disabled;
            return;
        }

        SavedSteamSession session;
        try
        {
            session = SteamSessionStore.Load(_sessionPath);
        }
        catch (Exception ex)
        {
            Logger.Info($"세션 파일 읽기 실패: {ex.Message} (Steam 로비 비활성).");
            _state = LobbyState.Disabled;
            return;
        }

        _manualRecycle = false;
        _loginFailures = 0;
        _retryBackoff = 0;

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>();
        _matchmaking = _steamClient.GetHandler<SteamMatchmaking>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _state = LobbyState.LoggingIn;
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = session.AccountName,
                AccessToken = session.RefreshToken,
                ShouldRememberPassword = true,
            });
        });
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamMatchmaking.CreateLobbyCallback>(OnLobbyCreated);

        Initialized = true;
        SetState(LobbyState.Connecting);
        _steamClient.Connect();
    }

    public void Tick()
    {
        _callbackManager?.RunCallbacks();

        if (_loggedOn && !_lobbyCreated && !_lobbyCreationPending
            && DateTime.UtcNow >= _lobbyRetryAfterUtc)
        {
            if (!_core.AuthInfoReceived)
            {
                _state = LobbyState.WaitAuth;
                if (DateTime.UtcNow - _lastAuthWaitWarnAtUtc >= AuthWaitWarnInterval)
                {
                    _lastAuthWaitWarnAtUtc = DateTime.UtcNow;
                    Logger.Info("AUTH_INFO 미수신 — 오케스트레이터 연결 대기 (로비 미생성). "
                        + "오케스트레이터가 게이트웨이를 등록했는지 확인하세요.");
                }
                _lobbyRetryAfterUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            }
            else
            {
                CreateLobbyNow();
            }
        }

        if (_lobbyCreated && DateTime.UtcNow - _lastMetadataRefresh > MetadataRefreshInterval)
        {
            RefreshDynamicMetadata();
        }

        if (_lobbyCreated && !_manualRecycle // 상태 정체 + 연결 비정상 -> 자가 재가동
            && DateTime.UtcNow - _lastStateChangeUtc >= SafetyRecycleInterval
            && !IsConnectionHealthy())
        {
            Logger.Info($"로비({_lobbyId}) 상태 정체 + 연결 비정상 감지 — 자가 재가동 (재로그인/재생성).");
            Recycle();
        }
    }

    public void Stop()
    {
        if (_steamClient == null)
        {
            return;
        }
        _manualRecycle = true; // Stop->Start 재가동 경로에서 자동 재연결과 충돌 방지
        _steamUser?.LogOff();
        _steamClient.Disconnect();
    }

    public void SendKickChatMessage(ulong targetSteamId, string reason)
    {
        if (!_lobbyCreated || _lobbyId == null || _matchmaking == null)
        {
            return;
        }

        string text = $"KICK:{targetSteamId}:{reason}";
        var msg = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(EMsg.ClientMMSSendLobbyChatMsg)
        {
            Body =
            {
                app_id = AppId,
                steam_id_lobby = _lobbyId,
                steam_id_target = 0,
                lobby_message = System.Text.Encoding.UTF8.GetBytes(text),
            },
        };
        _matchmaking.Send(msg, AppId);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            _loggedOn = false;
            _loginFailures++;
            _lobbyCreated = false;
            _lobbyCreationPending = false;
            SetState(LobbyState.Failed);
            ScheduleRetry();
            if (_loginFailures == 1 || _loginFailures % 5 == 0)
            {
                Logger.Info($"로그온 실패({_loginFailures}회): {callback.Result} / {callback.ExtendedResult}"
                    + " — 백오프 후 재시도.");
            }
            return;
        }

        _loggedOn = true;
        _loginFailures = 0;
        _retryBackoff = 0;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _state = _core.AuthInfoReceived ? LobbyState.Creating : LobbyState.WaitAuth;
    }

    private void ScheduleRetry()
    {
        TimeSpan delay = RetryDelays[Math.Min(_retryBackoff, RetryDelays.Length - 1)];
        _retryBackoff = Math.Min(_retryBackoff + 1, RetryDelays.Length - 1);
        _lobbyRetryAfterUtc = DateTime.UtcNow + delay;
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (_manualRecycle) return; // 자가 재가동 경로 - Tick의 Recycle이 재시작 담당

        bool hadLobby = _lobbyCreated;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _loggedOn = false;
        if (hadLobby)
        {
            Logger.Info($"CM 연결 끊김 — 로비({_lobbyId}) 손실 감지. 재연결/재생성 진행.");
        }
        _lobbyId = null;
        SetState(LobbyState.Connecting);
        _steamClient?.Connect();
    }

    private void SetState(LobbyState state)
    {
        if (_state == state) return;
        _state = state;
        _lastStateChangeUtc = DateTime.UtcNow;
    }

    private bool IsConnectionHealthy()
    {
        return _loggedOn && _steamClient != null;
    }

    private void Recycle()
    {
        Stop();
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _loggedOn = false;
        _lobbyId = null;
        _retryBackoff = 0;
        _loginFailures = 0;
        Start();
    }

    private void CreateLobbyNow()
    {
        if (_lobbyCreated || _lobbyCreationPending) return;
        _lobbyCreationPending = true;
        SetState(LobbyState.Creating);
        _matchmaking!.CreateLobby(AppId, ELobbyType.Public, _core.MaxPlayers,
            metadata: BuildBaseMetadata());
    }

    private void OnLobbyCreated(SteamMatchmaking.CreateLobbyCallback callback)
    {
        _lobbyCreationPending = false;
        if (callback.Result != EResult.OK)
        {
            Logger.Info($"로비 생성 실패: {callback.Result} — {CreateFailRetry.TotalSeconds:F0}초 후 재시도.");
            SetState(LobbyState.Failed);
            _lobbyRetryAfterUtc = DateTime.UtcNow + CreateFailRetry;
            return;
        }

        _lobbyId = callback.LobbySteamID;
        _lobbyCreated = true;
        _retryBackoff = 0;
        SetState(LobbyState.Created);
        Logger.Info($"로비 생성 성공: {_lobbyId} (AppID {callback.AppID})");
        RefreshDynamicMetadata();
    }

    private void RefreshDynamicMetadata()
    {
        _lastMetadataRefresh = DateTime.UtcNow;
        if (_lobbyId == null)
        {
            return;
        }

        _matchmaking!.SetLobbyData(AppId, _lobbyId, ELobbyType.Public, _core.MaxPlayers,
            metadata: BuildBaseMetadata());
    }

    private Dictionary<string, string> BuildBaseMetadata()
    {
        int avgMood = _livingCount > 0 ? _happinessSum / _livingCount : 0;

        return new()
        {
            [KeyLobbyName] = ClampName(_core.ServerName),
            [KeyVersion] = _versionTag,
            [KeyGamemode] = "loading",
            [KeyCurrentLayer] = "0",
            [KeyDepth] = "0",
            [KeyAvgMood] = avgMood.ToString(),
            [KeyLivingCount] = _livingCount.ToString(),
            [KeyPlrCount] = _core.SessionCount.ToString(),
            [KeyHasPassword] = _core.HasPassword ? "1" : "0",
            [KeyIsDedicated] = "1",
            [KeyLocked] = "0",
            [KeyRunSettings] = "0",
            [KeyDescription] = BuildDescription(),
            [KeyLanguage] = "EN",
            [KeyMods] = "CasualtiesMP",
            [KeyExtraData] = BuildExtraData(),
            ["bucket"] = Random.Shared.Next(0, 20).ToString(),
        };
    }

    private string ClampName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        if (name.Length <= MaxServerNameLength) return name;
        Logger.Info($"서버 이름이 {MaxServerNameLength}자를 초과하여 절단: \"{name}\"");
        return name.Substring(0, MaxServerNameLength);
    }

    private string BuildDescription()
    {
        if (string.IsNullOrEmpty(_motd)) return "";

        string[] lines = _motd.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string desc = string.Join("\n", lines.Take(MaxDescNewlines + 1));
        if (desc.Length > MaxDescLength)
            desc = desc.Substring(0, MaxDescLength);
        return desc;
    }

    private string BuildExtraData()
    {
        if (_rulesBytes == null || _rulesBytes.Length == 0)
        {
            return "";
        }

        ulong[] allSteamIds = _steamIds.Distinct().ToArray();

        var inner = new NetDataWriter();
        inner.Put(_rulesBytes);
        inner.PutArray(allSteamIds);
        inner.Put(false);   // SERVER_TOGGLE_ENFORCE_MODLIST

        byte[] compressed;
        using (var outStream = new MemoryStream())
        {
            using (var gzip = new GZipStream(outStream, CompressionLevel.Optimal))
            {
                gzip.Write(inner.CopyData());
            }
            compressed = outStream.ToArray();
        }

        var outer = new NetDataWriter();
        outer.PutBytesWithLength(compressed);
        return Convert.ToBase64String(outer.CopyData());
    }
}
