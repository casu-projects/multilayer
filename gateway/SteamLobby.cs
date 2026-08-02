using System.IO.Compression;
using System.Text.Json;
using LiteNetLib.Utils;
using SteamKit2;
using SteamKit2.Internal;

namespace CasuMpGateway;

/// <summary>SteamKit2 기반 로비 (PLAN.md G13 갭2 — 네트워크당 로비 1개).
/// 기존 오케스트레이터 SteamKitLobbyManager를 포팅한 것. P2P 데이터는 SteamLobbyAdapter가 처리하고,
/// 여기는 로비 생성/메타데이터/KICK 시그널만 담당한다.</summary>
public sealed class SteamLobby
{
    private const uint AppId = 4576510;

    private const string KeyLobbyName = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LOBBYNAME";
    private const string KeyVersion = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_VERSION";
    private const string KeyGamemode = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_GAMEMODE";
    private const string KeyCurrentLayer = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_CURRENTLAYER";
    private const string KeyAvgMood = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_AVGMOOD";
    private const string KeyDepth = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_DEPTH";
    private const string KeyLivingCount = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LIVINGCOUNT";
    private const string KeyPlrCount = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_PLRCOUNT";
    private const string KeyHasPassword = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_HASPASSWORD";
    private const string KeyIsDedicated = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_ISDEDICATED";
    private const string KeyLocked = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LOCKED";
    private const string KeyRunSettings = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_RUNSETTINGS";

    /// <summary>Must always be present (even empty) — the client's parser reads this key
    /// unconditionally and a missing key throws, hiding the whole lobby from search.</summary>
    private const string KeyExtraData = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_EXTRADATA";

    private readonly SteamConfig _config;
    private readonly Func<int> _connectedSessionCount;

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamMatchmaking? _matchmaking;
    private SteamID? _lobbyId;
    private bool _lobbyCreated;

    private DateTime _lastMetadataRefresh = DateTime.MinValue;
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(8);

    /// <summary>오케스트레이터의 LOBBY_METADATA 명령으로 갱신되는 동적 값 (플레이어 수는 코어 세션 수).</summary>
    private int _livingCount;
    private int _happinessSum;
    private ulong[] _steamIds = Array.Empty<ulong>();
    private byte[]? _rulesBytes;
    private string[] _modListGuids = Array.Empty<string>();
    private bool _enforceModList;

    public bool Initialized { get; private set; }

    public SteamLobby(SteamConfig config, Func<int> connectedSessionCount)
    {
        _config = config;
        _connectedSessionCount = connectedSessionCount;
    }

    /// <summary>오케스트레이터 LOBBY_METADATA 명령 적용 (PLAN.md — 인스턴스 리포트는 오케스트레이터가 수집).</summary>
    public void UpdateDynamicMetadata(GatewayCore.LobbyMetadataPayload payload)
    {
        _livingCount = payload.LivingCount;
        _happinessSum = payload.HappinessSum;
        _steamIds = payload.SteamIds ?? Array.Empty<ulong>();
        _modListGuids = payload.ModListGuids ?? Array.Empty<string>();
        _enforceModList = payload.EnforceModList;
        if (!string.IsNullOrEmpty(payload.RulesBase64))
        {
            try
            {
                _rulesBytes = Convert.FromBase64String(payload.RulesBase64);
            }
            catch (FormatException)
            {
                Log.Info("LOBBY_METADATA RulesBase64 파싱 실패.");
            }
        }
        RefreshDynamicMetadata();
    }

    public void Start()
    {
        if (!File.Exists(_config.SessionPath))
        {
            Log.Info($"세션 파일이 없습니다: {_config.SessionPath} (Steam 로비 비활성).");
            return;
        }

        SavedSteamSession session;
        try
        {
            string json = File.ReadAllText(_config.SessionPath);
            session = JsonSerializer.Deserialize<SavedSteamSession>(json)
                ?? throw new InvalidOperationException("세션 파일 파싱 결과가 비어있습니다.");
        }
        catch (Exception ex)
        {
            Log.Info($"세션 파일 읽기 실패: {ex.Message} (Steam 로비 비활성).");
            return;
        }

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>();
        _matchmaking = _steamClient.GetHandler<SteamMatchmaking>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = session.AccountName,
                AccessToken = session.RefreshToken,
                ShouldRememberPassword = true,
            });
        });
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            Log.Info("CM 연결 끊김 - 재접속 시도.");
            _lobbyCreated = false;
            _steamClient?.Connect();
        });
        _callbackManager.Subscribe<SteamMatchmaking.CreateLobbyCallback>(OnLobbyCreated);

        Initialized = true;
        _steamClient.Connect();
    }

    public void Tick()
    {
        _callbackManager?.RunCallbacks();

        if (_lobbyCreated && DateTime.UtcNow - _lastMetadataRefresh > MetadataRefreshInterval)
        {
            RefreshDynamicMetadata();
        }
    }

    public void Stop()
    {
        if (_steamClient == null)
        {
            return;
        }
        _steamUser?.LogOff();
        _steamClient.Disconnect();
    }

    /// <summary>바닐라 클라이언트가 표시하는 KICK 사유 — 로비 소유자의 로비 채팅 메시지
    /// "KICK:&lt;targetSteamId&gt;:&lt;reason&gt;" (KSteam.OnLobbyChatMsg — 소유자만 수용).</summary>
    public void SendKickChatMessage(ulong targetSteamId, string reason)
    {
        if (!_lobbyCreated || _lobbyId == null || _matchmaking == null)
        {
            Log.Info($"SendKickChatMessage 무시됨 (lobbyCreated={_lobbyCreated}) — "
                + $"대상 {targetSteamId}, 사유 \"{reason}\" 전달 안 됨.");
            return;
        }

        string text = $"KICK:{targetSteamId}:{reason}";
        Log.Info($"KICK 로비 채팅 메시지 전송: 내용=\"{text}\".");
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
            Log.Info($"로그온 실패: {callback.Result} / {callback.ExtendedResult}");
            return;
        }

        Log.Info($"로그인 성공 (SteamID {_steamClient!.SteamID}).");
        _matchmaking!.CreateLobby(AppId, ELobbyType.Public, _config.MaxPlayers,
            metadata: BuildBaseMetadata());
    }

    private void OnLobbyCreated(SteamMatchmaking.CreateLobbyCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            Log.Info($"로비 생성 실패: {callback.Result}");
            return;
        }

        _lobbyId = callback.LobbySteamID;
        _lobbyCreated = true;
        Log.Info($"로비 생성 성공: {_lobbyId} (AppID {callback.AppID})");
        RefreshDynamicMetadata();
    }

    private void RefreshDynamicMetadata()
    {
        _lastMetadataRefresh = DateTime.UtcNow;
        if (_lobbyId == null)
        {
            return;
        }

        _matchmaking!.SetLobbyData(AppId, _lobbyId, ELobbyType.Public, _config.MaxPlayers,
            metadata: BuildBaseMetadata());
    }

    private Dictionary<string, string> BuildBaseMetadata()
    {
        int avgMood = _livingCount > 0 ? _happinessSum / _livingCount : 0;

        return new()
        {
            [KeyLobbyName] = _config.ServerName,
            [KeyVersion] = _config.VersionTag,
            [KeyGamemode] = "loading",
            [KeyCurrentLayer] = "0",
            [KeyDepth] = "0",
            [KeyAvgMood] = avgMood.ToString(),
            [KeyLivingCount] = _livingCount.ToString(),
            [KeyPlrCount] = _connectedSessionCount().ToString(),
            [KeyHasPassword] = _config.HasPassword ? "1" : "0",
            [KeyIsDedicated] = "1",
            [KeyLocked] = "0",
            [KeyRunSettings] = "0",
            [KeyExtraData] = BuildExtraData(),
            ["bucket"] = Random.Shared.Next(0, 20).ToString(),
        };
    }

    /// <summary>EXTRADATA: gzip(rules + steamId[] + enforceModList + modListGuids[]), LiteNetLib
    /// 길이 프리픽스 + base64 — 클라이언트가 기대하는 형식 그대로.</summary>
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
        inner.Put(_enforceModList);
        inner.PutArray(_modListGuids);

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

/// <summary>tools/SteamLoginSetup가 저장하는 형식과 동일.</summary>
internal sealed class SavedSteamSession
{
    public string AccountName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}
