using System.IO.Compression;
using System.Text.Json;
using LiteNetLib.Utils;
using SteamKit2;
using SteamKit2.Internal;

namespace CasuMpGateway;

// SteamKit2 기반 로비(네트워크당 1개) - 생성/메타데이터/KICK 시그널만 담당한다.
// 로그인·AUTH_INFO·재시도 조건이 어떤 순서로 준비돼도 명시적 상태 머신 + 주기 안전망으로 로비가 수렴한다.
public sealed class SteamLobby
{
    private const uint AppId = 4576510;

    // 로비 수명주기 상태 - 자가 치유 판정에 사용
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
    private static readonly TimeSpan AuthWaitWarnInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SafetyRecycleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CreateFailRetry = TimeSpan.FromSeconds(10);

    // CasualtiesMP v5.0.0은 로비 데이터 키를 COOP_MOD_*로 통일했다. 클라이언트가 이 키를
    // 인덱서로 직접 읽으므로(누락 시 로비가 검색에서 숨겨짐) 키 이름을 맞춰야 한다.
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
    // 클라이언트가 GetValueOrDefault 로 읽는 신규 키 (없으면 각각 ""/"EN"/"" 폴백)
    private const string KeyDescription = "COOP_MOD_DESCRIPTION";
    private const string KeyLanguage = "COOP_MOD_LANGUAGE";
    private const string KeyMods = "COOP_MOD_MODS";

    // Must always be present (even empty) - the client's parser reads this key
    // unconditionally and a missing key throws, hiding the whole lobby from search
    private const string KeyExtraData = "COOP_MOD_EXTRADATA";

    private readonly string _sessionPath;
    private readonly string _versionTag;
    private readonly GatewayCore _core;

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamMatchmaking? _matchmaking;
    private bool _lobbyCreated;
    // 로비 생성 요청 발신 후 콜백 대기 중 - Tick 재발화 방지 (CreateLobby는 비동기라
    // 콜백 도착 전 매 틱 재생성 -> 로비 무한 생성 21+개)
    private bool _lobbyCreationPending;
    // 재시도 최소 간격 (실패/대기 시 틱마다 요청 폭주 방지)
    private DateTime _lobbyRetryAfterUtc;
    private bool _loggedOn;

    private LobbyState _state = LobbyState.Disabled;
    private SteamID? _lobbyId;
    private int _retryBackoff = 0;             // 0,1,2,... 지수 백오프 단계 (RetryMin*2^step, RetryMax 캡)
    private int _loginFailures;                // 연속 로그인 실패 횟수 (token 만료 의심 판정)
    private DateTime _lastAuthWaitWarnAtUtc = DateTime.MinValue;
    private DateTime _lastStateChangeUtc = DateTime.UtcNow;
    // 인위 재가동(Stop->Start) 중 플래그 - DisconnectedCallback의 자동 재연결과 충돌 방지
    private bool _manualRecycle;

    private DateTime _lastMetadataRefresh = DateTime.MinValue;
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(8);

    // 오케스트레이터의 LOBBY_METADATA 명령으로 갱신되는 동적 값 (플레이어 수는 코어 세션 수)
    // mod 목록은 전송하지 않는다 (EXTRADATA 와이어에는 steamId[] + false 고정)
    private int _livingCount;
    private int _happinessSum;
    private ulong[] _steamIds = Array.Empty<ulong>();
    private byte[]? _rulesBytes;
    private readonly string _motd;

    // 게임 Net 의 MAX_SERVER_DESC_LENGTH / MAX_SERVER_DESC_NEWLINES 와 일치해야 한다
    // (초과하면 클라이언트가 로비를 목록에서 제외한다)
    private const int MaxDescLength = 200;
    private const int MaxDescNewlines = 3;

    // 게임 Net.MAX_SERVER_NAME_LENGTH 와 일치 - 초과하면 클라이언트가 로비를 목록에서 제외한다
    private const int MaxServerNameLength = 32;

    public bool Initialized { get; private set; }

    public SteamLobby(string sessionPath, string versionTag, GatewayCore core, string motd)
    {
        _sessionPath = sessionPath;
        _versionTag = versionTag;
        _core = core;
        _motd = motd ?? "";
    }

    // 오케스트레이터 LOBBY_METADATA 명령 적용 (인스턴스 리포트는 오케스트레이터가 수집)
    public void UpdateDynamicMetadata(GatewayCore.LobbyMetadataPayload payload)
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
            string json = File.ReadAllText(_sessionPath);
            session = JsonSerializer.Deserialize<SavedSteamSession>(json)
                ?? throw new InvalidOperationException("세션 파일 파싱 결과가 비어있습니다.");
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

        // 로비 생성 게이트: SteamKit2 로그인 + AUTH_INFO가 모두 도착해야 생성 - 그 전에는
        // 서버명/인원/비밀번호 여부가 미확정 (코어 기본값 레이스 원천 제거)
        if (_loggedOn && !_lobbyCreated && !_lobbyCreationPending
            && DateTime.UtcNow >= _lobbyRetryAfterUtc)
        {
            if (!_core.AuthInfoReceived)
            {
                // AUTH_INFO 미수신 대기 (레이스 핵심): 오케스트레이터가 아직 준비되지 않아
                // AUTH_INFO를 못 받은 상태. 이 상태를 조용히 무한 대기로 남기지 않도록
                // (a) 상태를 WaitAuth로 명시하고 (b) 주기적으로 경고 로그를 남기며
                // (c) _lobbyRetryAfterUtc를 갱신해 AUTH_INFO 도착 즉시 다음 Tick에서 생성되게 한다
                _state = LobbyState.WaitAuth;
                if (DateTime.UtcNow - _lastAuthWaitWarnAtUtc >= AuthWaitWarnInterval)
                {
                    _lastAuthWaitWarnAtUtc = DateTime.UtcNow;
                    Logger.Info("AUTH_INFO 미수신 — 오케스트레이터 연결 대기 (로비 미생성). "
                        + "오케스트레이터가 게이트웨이를 등록했는지 확인하세요.");
                }
                // 매 틱 재진입을 막되, AUTH_INFO 도착 시 즉시 생성되도록 짧은 간격 갱신
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

        // 안전망: 생성된 로비(Created)가 SafetyRecycleInterval 동안 상태 전이가 없고
        // (로그온/CM 연결 끊김 등으로 로비가 조용히 사라진 케이스) 내부 연결이 죽어 있으면
        // 자가 재가동(Stop->Start)으로 재로그인 + 재생성을 유도한다. SteamKit2는 로비가
        // 삭제되어도 콜백을 주지 않으므로, 폴링은 "연결/로그온 상태 + 상태 정체" 기반으로만
        // 판정한다.
        if (_lobbyCreated && !_manualRecycle
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

    // 바닐라 클라이언트가 표시하는 KICK 사유 - 로비 소유자의 로비 채팅 메시지
    // "KICK:&lt;targetSteamId&gt;:&lt;reason&gt;" (KSteam.OnLobbyChatMsg - 소유자만 수용)
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
            // 연속 실패 시 지수 백오프 + 재로그온 유도. 실패는 초기엔 Info, 반복 시 주기 경고
            // (token 만료/세션 무효 등 지속 실패 원인 파악용 - 재시작마다 반복되면 refresh
            // token 재발급이 필요하다는 신호)
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
        // 로비 생성은 Tick의 게이트에서 (AUTH_INFO 수신 후) 수행한다
    }

    // 로그인 실패/CM 끊김 후 재시도 예약 - 지수 백오프 (2s * 2^step, 최대 15s 캡)
    private void ScheduleRetry()
    {
        var delay = TimeSpan.FromSeconds(
            Math.Min(RetryMin.TotalSeconds * Math.Pow(2, _retryBackoff), RetryMax.TotalSeconds));
        _retryBackoff = Math.Min(_retryBackoff + 1, 10);
        _lobbyRetryAfterUtc = DateTime.UtcNow + delay;
    }

    // CM 연결 끊김 - 로비 상태 초기화 후 재연결 (TryAnotherCM 포함 자동 CM 전환) + 백오프
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
        // 재연결 시도 (백오프 없이 즉시 - SteamKit2가 TryAnotherCM으로 CM을 순회)
        _steamClient?.Connect();
    }

    // 상태 전이 기록 - 자가 치유/진단용 상태 변화 타임스탬프 갱신
    private void SetState(LobbyState state)
    {
        if (_state == state) return;
        _state = state;
        _lastStateChangeUtc = DateTime.UtcNow;
    }

    // 연결/로그온 상태 건강성 - 로비 사망 감지용 (로그인됐으면 살아있는 것으로 간주)
    private bool IsConnectionHealthy()
    {
        return _loggedOn && _steamClient != null;
    }

    // 자가 재가동 - Stop(로그오프/연결 해제) 후 재시작(재로그인 + 재생성)
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

    // 서버 이름을 게임 제한(32자)으로 클램프 - 초과 시 클라이언트가 로비를 목록에서 제외한다
    private string ClampName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        if (name.Length <= MaxServerNameLength) return name;
        Logger.Info($"서버 이름이 {MaxServerNameLength}자를 초과하여 절단: \"{name}\"");
        return name.Substring(0, MaxServerNameLength);
    }

    // gateway.json Motd(문자열) -> COOP_MOD_DESCRIPTION.
    // 게임 제한(최대 4줄=개행 3개, 200자)을 넘지 않도록 클램프한다.
    private string BuildDescription()
    {
        if (string.IsNullOrEmpty(_motd)) return "";

        string[] lines = _motd.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string desc = string.Join("\n", lines.Take(MaxDescNewlines + 1));
        if (desc.Length > MaxDescLength)
            desc = desc.Substring(0, MaxDescLength);
        return desc;
    }

    // EXTRADATA: gzip(deathcounter+rules struct + steamId[] + enforceModList), LiteNetLib
    // 길이 프리픽스 + base64 - 클라이언트가 기대하는 형식
    // (클라이언트 파서는 modlist를 COOP_MOD_MODS 키에서 읽으므로 EXTRADATA에 mod/MOTD 배열을 넣지 않는다)
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

// tools/SteamLoginSetup가 저장하는 형식과 동일
internal sealed class SavedSteamSession
{
    public string AccountName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}
