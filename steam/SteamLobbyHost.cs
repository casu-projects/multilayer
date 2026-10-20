using System.IO.Compression;
using CasuMpSteam.Session;
using LiteNetLib.Utils;
using SteamKit2;
using Polly.Contrib.WaitAndRetry;
using SteamKit2.Internal;

namespace CasuMpSteam;

public sealed class SteamLobbyHost
{
    private const uint AppId = 4576510;

    public enum LobbyState
    {
        Disabled,
        Connecting,
        LoggingIn,
        WaitAuth,
        Creating,
        Created,
        Failed,
    }

    private static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] RetryDelays = Backoff.DecorrelatedJitterBackoffV2(RetryMin, 10)
        .Select(delay => delay > RetryMax ? RetryMax : delay)
        .ToArray();
    private static readonly TimeSpan AuthWaitWarnInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SafetyRecycleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CreateFailRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(90);

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

    private const int MaxDescLength = 200;
    private const int MaxDescNewlines = 3;
    private const int MaxServerNameLength = 32;

    private readonly string _sessionPath;
    private readonly string _fallbackServerName;
    private string _versionTag = "";
    private string _motd = "";

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
    private int _retryBackoff;
    private int _loginFailures;
    private DateTime _lastAuthWaitWarnAtUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.UtcNow;
    private bool _manualRecycle;

    private DateTime _lastMetadataRefresh = DateTime.MinValue;
    private DateTime _lastMetadataAtUtc = DateTime.MinValue;
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(8);

    private bool _metadataReceived;
    private string _serverName;
    private bool _hasPassword;
    private int _maxPlayers;
    private int _playerCount;
    private int _livingCount;
    private int _happinessSum;
    private ulong[] _steamIds = Array.Empty<ulong>();
    private byte[]? _rulesBytes;

    private TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>? _pendingLobbyList;
    private bool _lobbyListSent;

    public bool Initialized { get; private set; }

    public LobbyState State => _state;

    public bool LoggedOn => _loggedOn;

    public ulong? LobbyId => _lobbyId?.ConvertToUInt64();

    public SteamLobbyHost(string sessionPath, string fallbackServerName)
    {
        _sessionPath = sessionPath;
        _fallbackServerName = fallbackServerName;
        _serverName = fallbackServerName;
        _maxPlayers = 32;
    }

    public void UpdateMetadata(LobbyMetadataPayload payload)
    {
        _metadataReceived = true;
        _lastMetadataAtUtc = DateTime.UtcNow;
        _serverName = string.IsNullOrEmpty(payload.ServerName) ? _fallbackServerName : payload.ServerName;
        if (!string.IsNullOrEmpty(payload.VersionTag)) _versionTag = payload.VersionTag;
        if (payload.Motd != null) _motd = payload.Motd;
        if (payload.MaxPlayers > 0) _maxPlayers = payload.MaxPlayers;
        _hasPassword = payload.HasPassword;
        _playerCount = payload.PlayerCount;
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
                SteamLog.Info("LOBBY_METADATA RulesBase64 파싱 실패.");
            }
        }
        if (_lobbyCreated) RefreshDynamicMetadata();
    }

    public void Start()
    {
        Initialized = false;
        _manualRecycle = false;
        _loginFailures = 0;
        _retryBackoff = 0;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _loggedOn = false;
        _lobbyId = null;

        if (!File.Exists(_sessionPath))
        {
            SteamLog.Info($"세션 파일이 없습니다: {_sessionPath} (Steam 로비 비활성).");
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
            SteamLog.Info($"세션 파일 읽기 실패: {ex.Message} (Steam 로비 비활성).");
            _state = LobbyState.Disabled;
            return;
        }

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
        _callbackManager.Subscribe<SteamMatchmaking.GetLobbyListCallback>(OnLobbyListReceived);

        Initialized = true;
        SetState(LobbyState.Connecting);
        _steamClient.Connect();
    }

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        _callbackManager?.RunCallbacks();

        if (Volatile.Read(ref _pendingLobbyList) != null && _loggedOn
            && !Volatile.Read(ref _lobbyListSent) && _matchmaking != null)
        {
            Volatile.Write(ref _lobbyListSent, true);
            var filters = new List<SteamMatchmaking.Lobby.Filter>
            {
                new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide)
            };
            _matchmaking.GetLobbyList(AppId, filters, maxLobbies: 50);
        }

        bool metadataFresh = _metadataReceived && now - _lastMetadataAtUtc <= MetadataTimeout;
        bool versionKnown = !string.IsNullOrEmpty(_versionTag);

        if (_loggedOn && !_lobbyCreated && !_lobbyCreationPending
            && now >= _lobbyRetryAfterUtc)
        {
            if (!metadataFresh || !versionKnown)
            {
                _state = LobbyState.WaitAuth;
                if (now - _lastAuthWaitWarnAtUtc >= AuthWaitWarnInterval)
                {
                    _lastAuthWaitWarnAtUtc = now;
                    SteamLog.Info(metadataFresh
                        ? "버전 태그 미수신 — 프리웜 레이어 보고 대기 (로비 미생성)."
                        : "코어 메타데이터 미수신 — casu-server 연결 대기 (로비 미생성).");
                }
                _lobbyRetryAfterUtc = now + TimeSpan.FromSeconds(1);
            }
            else
            {
                CreateLobbyNow();
            }
        }

        if (_lobbyCreated && !metadataFresh)
        {
            CloseLobby();
        }
        else if (_lobbyCreated && now - _lastMetadataRefresh > MetadataRefreshInterval)
        {
            RefreshDynamicMetadata();
        }

        if (_lobbyCreated && !_manualRecycle
            && now - _lastStateChangeUtc >= SafetyRecycleInterval
            && !IsConnectionHealthy())
        {
            SteamLog.Info($"로비({_lobbyId}) 상태 정체 + 연결 비정상 감지 — 자가 재가동 (재로그인/재생성).");
            Recycle();
        }
    }

    public void Stop()
    {
        _pendingLobbyList?.TrySetCanceled();
        _pendingLobbyList = null;
        if (_steamClient == null) return;
        Initialized = false;
        _manualRecycle = true;
        _steamUser?.LogOff();
        _steamClient.Disconnect();
    }

    public void StopAndReset()
    {
        Stop();
        _state = LobbyState.Disabled;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _loggedOn = false;
        _lobbyId = null;
        _retryBackoff = 0;
        _loginFailures = 0;
    }

    public void Recycle()
    {
        StopAndReset();
        Start();
    }

    public async Task<SteamMatchmaking.GetLobbyListCallback> GetLobbyListAsync(CancellationToken ct)
    {
        if (!_loggedOn || _matchmaking == null)
            throw new InvalidOperationException("Steam login is not ready.");

        var tcs = new TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pendingLobbyList, tcs, null) != null)
            throw new InvalidOperationException("A lobby list request is already in progress.");
        Volatile.Write(ref _lobbyListSent, false);

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingLobbyList, null, tcs);
            Volatile.Write(ref _lobbyListSent, false);
        }
    }

    public void SendKickChatMessage(ulong targetSteamId, string reason)
    {
        if (!_lobbyCreated || _lobbyId == null || _matchmaking == null) return;

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

    private void OnLobbyListReceived(SteamMatchmaking.GetLobbyListCallback callback)
    {
        Volatile.Write(ref _lobbyListSent, false);
        TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>? pending =
            Interlocked.Exchange(ref _pendingLobbyList, null);
        pending?.TrySetResult(callback);
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
                SteamLog.Info($"로그온 실패({_loginFailures}회): {callback.Result} / {callback.ExtendedResult}"
                    + " — 백오프 후 재시도.");
            }
            return;
        }

        _loggedOn = true;
        _loginFailures = 0;
        _retryBackoff = 0;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _state = _metadataReceived && DateTime.UtcNow - _lastMetadataAtUtc <= MetadataTimeout
            && !string.IsNullOrEmpty(_versionTag)
            ? LobbyState.Creating
            : LobbyState.WaitAuth;
    }

    private void ScheduleRetry()
    {
        TimeSpan delay = RetryDelays[Math.Min(_retryBackoff, RetryDelays.Length - 1)];
        _retryBackoff = Math.Min(_retryBackoff + 1, RetryDelays.Length - 1);
        _lobbyRetryAfterUtc = DateTime.UtcNow + delay;
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _pendingLobbyList?.TrySetCanceled();
        _pendingLobbyList = null;
        Volatile.Write(ref _lobbyListSent, false);

        if (_manualRecycle) return;

        bool hadLobby = _lobbyCreated;
        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _loggedOn = false;
        if (hadLobby)
        {
            SteamLog.Info($"CM 연결 끊김 — 로비({_lobbyId}) 손실 감지. 재연결/재생성 진행.");
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

    private bool IsConnectionHealthy() => _loggedOn && _steamClient != null;

    private void CloseLobby()
    {
        if (!_lobbyCreated || _lobbyId == null) return;
        SteamID closingId = _lobbyId;
        try
        {
            _matchmaking?.LeaveLobby(AppId, closingId);
        }
        catch (Exception ex)
        {
            SteamLog.Info($"로비 등록 해제 실패: {ex.Message}");
        }

        _lobbyCreated = false;
        _lobbyCreationPending = false;
        _lobbyId = null;
        _lastMetadataRefresh = DateTime.MinValue;
        _lobbyRetryAfterUtc = DateTime.UtcNow;
        SetState(LobbyState.WaitAuth);
        SteamLog.Info($"코어 메타데이터 끊김 — 로비 등록 해제: {closingId.ConvertToUInt64()}");
    }

    private void CreateLobbyNow()
    {
        if (_lobbyCreated || _lobbyCreationPending) return;
        _lobbyCreationPending = true;
        SetState(LobbyState.Creating);
        _matchmaking!.CreateLobby(AppId, ELobbyType.Public, Math.Max(1, _maxPlayers),
            metadata: BuildBaseMetadata());
    }

    private void OnLobbyCreated(SteamMatchmaking.CreateLobbyCallback callback)
    {
        _lobbyCreationPending = false;
        if (callback.Result != EResult.OK)
        {
            SteamLog.Info($"로비 생성 실패: {callback.Result} — {CreateFailRetry.TotalSeconds:F0}초 후 재시도.");
            SetState(LobbyState.Failed);
            _lobbyRetryAfterUtc = DateTime.UtcNow + CreateFailRetry;
            return;
        }

        _lobbyId = callback.LobbySteamID;
        _lobbyCreated = true;
        _retryBackoff = 0;
        SetState(LobbyState.Created);
        SteamLog.Info($"로비 생성 성공: {_lobbyId} (AppID {callback.AppID})");
        RefreshDynamicMetadata();
    }

    private void RefreshDynamicMetadata()
    {
        _lastMetadataRefresh = DateTime.UtcNow;
        if (_lobbyId == null) return;

        _matchmaking!.SetLobbyData(AppId, _lobbyId, ELobbyType.Public, Math.Max(1, _maxPlayers),
            metadata: BuildBaseMetadata());
    }

    private Dictionary<string, string> BuildBaseMetadata()
    {
        int avgMood = _livingCount > 0 ? _happinessSum / _livingCount : 0;

        return new()
        {
            [KeyLobbyName] = ClampName(_serverName),
            [KeyVersion] = _versionTag,
            [KeyGamemode] = "loading",
            [KeyCurrentLayer] = "0",
            [KeyDepth] = "0",
            [KeyAvgMood] = avgMood.ToString(),
            [KeyLivingCount] = _livingCount.ToString(),
            [KeyPlrCount] = _playerCount.ToString(),
            [KeyHasPassword] = _hasPassword ? "1" : "0",
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
        SteamLog.Info($"서버 이름이 {MaxServerNameLength}자를 초과하여 절단: \"{name}\"");
        return name.Substring(0, MaxServerNameLength);
    }

    private string BuildDescription()
    {
        if (string.IsNullOrEmpty(_motd)) return "";

        string[] lines = _motd.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string desc = string.Join("\n", lines.Take(MaxDescNewlines + 1));
        if (desc.Length > MaxDescLength) desc = desc.Substring(0, MaxDescLength);
        return desc;
    }

    private string BuildExtraData()
    {
        if (_rulesBytes == null || _rulesBytes.Length == 0) return "";

        ulong[] allSteamIds = _steamIds.Distinct().ToArray();

        var inner = new NetDataWriter();
        inner.Put(_rulesBytes);
        inner.PutArray(allSteamIds);
        inner.Put(false);

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
