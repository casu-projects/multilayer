using System.Collections.Concurrent;
using System.Text.Json;

namespace CasuMpGateway;

// 게이트웨이 코어 - 세션 레지스트리 + 라우팅 미러 + 제어 명령 처리
// 모든 게임 데이터 경로 처리는 메인 루프 스레드에서만 일어난다
public sealed class GatewayCore
{
    public GatewayConfig Config { get; }

    private readonly Dictionary<PlayerKey, ClientSession> _sessions = new();
    private readonly Dictionary<PlayerKey, RouteEntry> _routes = new();
    private readonly HashSet<PlayerKey> _banned = new();
    private readonly object _lock = new();

    private readonly ConcurrentQueue<ControlMessage> _inbound = new();
    private readonly ConcurrentQueue<ControlMessage> _outbound = new();
    private long _seqCounter;

    private bool _maintenance;
    private string _maintenanceMessage = "";
    private readonly HashSet<ulong> _maintenanceBypass = new();
    private LobbyMetadataPayload? _lastLobbyMetadata;
    
    private string _serverPassword = "";
    private int _maxPlayers = 32;
    private string _serverName = "CasuMP Server";

    public GatewayCore(GatewayConfig config)
    {
        Config = config;
    }

    public bool IsMaintenance => _maintenance;
    public string MaintenanceMessage => _maintenanceMessage;

    public int MaxPlayers => _maxPlayers;

    public string ServerName => _serverName;

    public bool HasPassword => !string.IsNullOrEmpty(_serverPassword);

    // AUTH_INFO 수신 여부 - 수신 전에는 서버명/인원/비밀번호 여부가 미확정이라 로비를 만들지 않는다
    public bool AuthInfoReceived { get; private set; }

    // 서버 비밀번호 검증 - 미설정이면 항상 허용 (게임 서버가 최종 권위)
    public bool ValidatePassword(string? candidate) =>
        string.IsNullOrEmpty(_serverPassword) || candidate == _serverPassword;

    // 오케스트레이터 종료 신호 - 전 세션 정리 후 프로세스 종료 요청
    public event Action? ShutdownRequested;

    private void ApplyShutdown()
    {
        lock (_lock)
        {
            foreach (ClientSession session in _sessions.Values.ToList())
            {
                session.Kick("Server is shutting down.");
                RemoveSession(session, "shutdown");
            }
        }
        Logger.Info("종료 신호 수신 — 서버 종료.");
        ShutdownRequested?.Invoke();
    }

    // 활성 세션 수 (로비 PLRCOUNT 메타데이터용)
    public int SessionCount => _sessions.Count;

    // 오케스트레이터 LOBBY_METADATA 명령 -> Steam 어댑터 전달 (없으면 무시)
    public Action<LobbyMetadataPayload>? OnLobbyMetadata { get; set; }

    // 로비 상태 조회 (LOBBY_STATUS 응답용) - Program이 SteamLobbyAdapter로 배선.
    // Steam 비활성 시 null -> 응답에 steamEnabled=false 반영
    public Func<LobbyStatusSnapshot?>? LobbyStatusProvider { get; set; }

    // 로비 상태 스냅샷 (진단 연동 - 오케스트레이터 LOBBY_STATUS 응답 payload)
    public sealed class LobbyStatusSnapshot
    {
        public bool SteamEnabled { get; set; }
        public string State { get; set; } = "";
        public string? LobbyId { get; set; }
        public bool LoggedOn { get; set; }
        public bool AuthInfoReceived { get; set; }
        public bool SteamApiInitialized { get; set; }
    }

    // 제어 채널 (ControlChannel)

    public void EnqueueCommand(ControlMessage msg) => _inbound.Enqueue(msg);

    public ControlMessage? TryDequeueOutbound() => _outbound.TryDequeue(out var msg) ? msg : null;

    public long NextSeq() => ++_seqCounter;

    // 보고 메시지 (best-effort, ack 불필요)
    public void Report(string type, object? payload) =>
        _outbound.Enqueue(ControlMessage.Create(NextSeq(), type, payload));

    // 재연결 후 활성 세션 전부 재보고
    public void ReportActiveSessions()
    {
        lock (_lock)
        {
            foreach (ClientSession session in _sessions.Values)
            {
                Report("SESSION_CONNECTED", new
                {
                    playerKey = session.Player.Value,
                    steamId = session.SteamId,
                    username = session.Username,
                });
            }
        }
    }

    // 메인 루프 틱 - 제어 명령 처리 + 세션 틱
    public void Tick()
    {
        while (_inbound.TryDequeue(out ControlMessage? msg) && msg != null)
        {
            HandleCommand(msg);
        }

        List<ClientSession> snapshot;
        lock (_lock)
        {
            snapshot = _sessions.Values.ToList();
        }
        foreach (ClientSession session in snapshot)
        {
            session.Tick();
            if (session.State == SessionState.Routing
                && session.RoutingWaitStartedAt.HasValue
                && DateTime.UtcNow - session.RoutingWaitStartedAt.Value > TimeSpan.FromSeconds(Config.RoutingWaitTimeoutSeconds))
            {
                Logger.Info($"{session.Username} 라우팅 대기 타임아웃 — 거부.");
                session.Kick("Server is busy, please try again.");
                RemoveSession(session, "routing timeout");
            }
        }
    }

    // 세션 수용/종료 (어댑터 호출)

    // 어댑터가 새 세션을 전달 (ACCEPTED). 같은 플레이어의 기존 세션은 폐기
    public void AcceptSession(ClientSession session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.Player, out ClientSession? old))
            {
                Logger.Debug($"{session.Username} 중복 세션 — 기존 세션 폐기.");
                _sessions.Remove(session.Player);
                old.Kick("Reconnected from a new session.");
            }
            _sessions[session.Player] = session;
        }

        Report("SESSION_CONNECTED", new
        {
            playerKey = session.Player.Value,
            steamId = session.SteamId,
            username = session.Username,
        });

        if (_maintenance && !IsBypassed(session))
        {
            session.Kick(_maintenanceMessage.Length > 0 ? _maintenanceMessage : "Server is in maintenance mode.");
            RemoveSession(session, "maintenance");
            return;
        }
        if (_banned.Contains(session.Player))
        {
            session.Kick("You are banned from this server.");
            RemoveSession(session, "banned");
            return;
        }

        // 비밀번호/인원 검증 - Steam 경로의 유일한 방어선 (DirectIpAdapter는 접속 시도 단계에서
        // 이미 거부하므로 중복 없음). 사유는 Kick -> 로비 채팅 KICK으로 전달된다. 본 세션은 이미
        // _sessions에 추가된 상태라 인원 비교는 ">" (DirectIpAdapter는 미포함이라 ">=")
        if (!ValidatePassword(session.Password))
        {
            session.Kick("Wrong password.");
            RemoveSession(session, "wrong password");
            return;
        }
        if (SessionCount > MaxPlayers)
        {
            session.Kick("Server is full.");
            RemoveSession(session, "server full");
            return;
        }

        lock (_lock)
        {
            // 라우팅 결정은 오케스트레이터만 - 미러 라우트를 즉시 쓰면 재접속 플레이어가 스테일
            // 라우트(이전 레이어/정지 인스턴스)로 오접속된다. 웜 인스턴스는 SESSION_CONNECTED
            // 직후 ROUTE_UPDATE가 즉시 도착하므로 대기 비용이 없다
            session.EnterRoutingWait();
        }
    }

    // 백엔드 미연결 세션의 클라이언트 이탈 (어댑터 호출)
    public void CloseSession(ClientSession session, string reason)
    {
        if (session.Disposed) return;
        session.Dispose();
        RemoveSession(session, reason);
    }
    // 세션 이벤트 (ClientSession -> 코어)
    public void OnSessionBackendConnected(ClientSession session)
    {
        Report("BACKEND_CONNECTED", new
        {
            playerKey = session.Player.Value,
            instanceId = session.InstanceId,
        });
    }

    public void OnSessionBackendFailed(ClientSession session, string reason)
    {
        session.Kick(reason);
        RemoveSession(session, reason);
    }

    // 내부

    private void HandleCommand(ControlMessage msg)
    {
        bool ok = true;
        string? reason = null;
        try
        {
            switch (msg.Type)
            {
                case "TABLE_SNAPSHOT":
                    ApplyTableSnapshot(msg);
                    break;
                case "ROUTE_UPDATE":
                    ApplyRouteUpdate(msg);
                    break;
                case "SWAP":
                    ApplySwap(msg);
                    break;
                case "KICK":
                    ApplyKick(msg);
                    break;
                case "BAN":
                    ApplyBan(msg);
                    break;
                case "AUTH_INFO":
                    ApplyAuthInfo(msg);
                    break;
                case "MAINTENANCE":
                    ApplyMaintenance(msg);
                    break;
                case "VERBOSE":
                    ApplyVerbose(msg);
                    break;
                case "SHUTDOWN":
                    ApplyShutdown();
                    break;
                case "LOBBY_METADATA":
                    ApplyLobbyMetadata(msg);
                    break;
                case "LOBBY_STATUS":
                    ApplyLobbyStatusRequest(msg);
                    break;
                default:
                    ok = false;
                    reason = $"unknown type: {msg.Type}";
                    break;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            reason = ex.Message;
            Logger.Info($"명령 처리 실패 ({msg.Type}): {ex}");
        }

        _outbound.Enqueue(ControlMessage.Ack(msg.Seq, ok, reason));
    }

    private void ApplyTableSnapshot(ControlMessage msg)
    {
        var entries = msg.PayloadAs<TableSnapshot>() ?? throw new InvalidDataException("payload 없음");
        lock (_lock)
        {
            _routes.Clear();
            foreach (RouteEntry entry in entries.Entries)
            {
                _routes[PlayerKey.FromString(entry.PlayerKey)] = entry;
            }
        }
    }

    private void ApplyRouteUpdate(ControlMessage msg)
    {
        var entry = msg.PayloadAs<RouteEntry>() ?? throw new InvalidDataException("payload 없음");
        lock (_lock)
        {
            _routes[PlayerKey.FromString(entry.PlayerKey)] = entry;
            // Routing 대기 세션 + 아직 백엔드에 한 번도 연결 성공하지 못한 Connecting 세션을
            // 재라우팅한다 (죽은 주소로 재시도 중인 세션이 새 인스턴스 주소로 전환 - 방어 2)
            if (_sessions.TryGetValue(PlayerKey.FromString(entry.PlayerKey), out ClientSession? session)
                && IsUsableBackendAddr(entry.BackendAddr)
                && (session.State == SessionState.Routing
                    || (session.State == SessionState.Connecting && !session.HasEverConnectedToBackend)))
            {
                session.BeginRoute(entry.BackendAddr, entry.ClientId, entry.IsReturning, entry.InstanceId);
            }
        }
    }

    private void ApplySwap(ControlMessage msg)
    {
        var payload = msg.PayloadAs<SwapPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);

        ClientSession? session;
        lock (_lock)
        {
            // 라우트 테이블도 함께 갱신 - SWAP 후 재접속 시 스테일 라우트(이전 인스턴스)로
            // 즉시 연결되는 것 방지 (스테일 라우트로 연결 성공하면 ROUTE_UPDATE 재라우팅이
            // 무효화되어 이전 레이어에 고정됨). 세션 유무와 무관하게 먼저 갱신
            if (_routes.TryGetValue(key, out RouteEntry? existing))
            {
                _routes[key] = new RouteEntry
                {
                    PlayerKey = existing.PlayerKey,
                    InstanceId = payload.InstanceId,
                    ClientId = existing.ClientId,
                    BackendAddr = payload.BackendAddr,
                    IsReturning = existing.IsReturning,
                };
            }

            if (!_sessions.TryGetValue(key, out session))
            {
                // 활성 세션 없음 - 마이그레이션 대상이 이미 접속 종료. 오케스트레이터가 상태 정리
                return;
            }
        }
        Logger.Debug($"{session.Username} 세션을 {payload.BackendAddr}(으)로 전환 (클라 연결 유지).");
        // SWAP 멱등성 : 이미 같은 목적지로 Active/Connecting/Swapping이면
        // 재연결하지 않는다 - 중복 SWAP(오케스트레이터 재전송/재시도)으로 백엔드를 이중
        // 연결하면 인스턴스의 "Player with this name already exists" 추방이 발생한다
        if (session.BackendAddr == payload.BackendAddr
            && session.State is SessionState.Active or SessionState.Connecting or SessionState.Swapping)
        {
            return;
        }
        session.SwapBackend(payload.BackendAddr, payload.InstanceId);
    }

    private void ApplyKick(ControlMessage msg)
    {
        var payload = msg.PayloadAs<KickPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out ClientSession? session))
            {
                session.Kick(payload.Reason ?? "Kicked.");
                RemoveSession(session, "kick");
            }
        }
    }

    private void ApplyBan(ControlMessage msg)
    {
        var payload = msg.PayloadAs<BanPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);
        lock (_lock)
        {
            if (payload.Banned)
            {
                _banned.Add(key);
                if (_sessions.TryGetValue(key, out ClientSession? session))
                {
                    session.Kick("You are banned from this server.");
                    RemoveSession(session, "banned");
                }
            }
            else
            {
                _banned.Remove(key);
            }
        }
    }

    // AUTH_INFO (연결/재연결 시 스냅샷) - 밴 목록 단일 소유자는 오케스트레이터, 여기선 메모리
    // 사본 교체 + 서버 비밀번호/최대 인원 저장 (파일 영속화 없음 - 재수신으로 수렴)
    private void ApplyAuthInfo(ControlMessage msg)
    {
        var payload = msg.PayloadAs<AuthInfoPayload>() ?? throw new InvalidDataException("payload 없음");
        lock (_lock)
        {
            _serverPassword = payload.ServerPassword ?? "";
            _maxPlayers = payload.MaxPlayers > 0 ? payload.MaxPlayers : 32;
            if (!string.IsNullOrEmpty(payload.ServerName))
            {
                _serverName = payload.ServerName!;
                // 서버명 변경(락다운 (MAINTENANCE) 접미 등) - 저장된 메타데이터 재푸시로 로비 이름 즉시 반영
                if (_lastLobbyMetadata != null)
                {
                    OnLobbyMetadata?.Invoke(_lastLobbyMetadata);
                }
            }
            _banned.Clear();
            foreach (string key in payload.BannedKeys ?? [])
            {
                _banned.Add(PlayerKey.FromString(key));
            }
            AuthInfoReceived = true;
        }
    }

    // 디버그 로그 표시 상태 (오케스트레이터 VERBOSE 메시지)
    private void ApplyVerbose(ControlMessage msg)
    {
        var payload = msg.PayloadAs<VerbosePayload>();
        Logger.Verbose = payload?.On ?? false;
        Logger.Info($"verbose {(Logger.Verbose ? "켬" : "끔")} (오케스트레이터).");
    }

    private void ApplyMaintenance(ControlMessage msg)    {
        var payload = msg.PayloadAs<MaintenancePayload>() ?? throw new InvalidDataException("payload 없음");
        _maintenance = payload.On;
        _maintenanceMessage = payload.Message ?? "";
        _maintenanceBypass.Clear();
        if (payload.Bypass != null)
        {
            foreach (ulong id in payload.Bypass)
            {
                if (id != 0) _maintenanceBypass.Add(id);
            }
        }
        Logger.Info($"유지보수 모드 {(payload.On ? "켬" : "끔")} (bypass {_maintenanceBypass.Count}명).");

        // 락다운 진입 - 현재 접속 세션 전체 추방 (bypass 제외)
        if (payload.On && payload.KickAll)
        {
            lock (_lock)
            {
                foreach (ClientSession session in _sessions.Values.ToList())
                {
                    if (session.Disposed || IsBypassed(session)) continue;
                    session.Kick("Server entered maintenance mode");
                    RemoveSession(session, "maintenance");
                }
            }
        }
    }

    // 락다운 bypass 판정 - SteamID64가 허용 목록에 있으면 유지보수 중에도 통과
    private bool IsBypassed(ClientSession session) =>
        session.SteamId is ulong sid && _maintenanceBypass.Contains(sid);

    private void ApplyLobbyMetadata(ControlMessage msg)
    {
        var payload = msg.PayloadAs<LobbyMetadataPayload>() ?? throw new InvalidDataException("payload 없음");
        _lastLobbyMetadata = payload;
        OnLobbyMetadata?.Invoke(payload);
    }

    // LOBBY_STATUS 요청 -> 현재 로비 상태 스냅샷 응답 (진단용 - 자가 치유는 게이트웨이 내부
    // Tick의 SteamLobby가 독립 수행하므로 여기선 조회/표시만 담당)
    private void ApplyLobbyStatusRequest(ControlMessage msg)
    {
        var snap = LobbyStatusProvider?.Invoke();
        Report("LOBBY_STATUS_RESPONSE", new
        {
            state = snap?.State ?? "Disabled",
            lobbyId = snap?.LobbyId,
            loggedOn = snap?.LoggedOn ?? false,
            authInfoReceived = snap?.AuthInfoReceived ?? AuthInfoReceived,
            steamEnabled = snap?.SteamEnabled ?? false,
            steamApiInitialized = snap?.SteamApiInitialized ?? false,
        });
    }

    private void RemoveSession(ClientSession session, string reason)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.Remove(session.Player);
        }
        if (removed)
        {
            Report("SESSION_DISCONNECTED", new
            {
                playerKey = session.Player.Value,
                reason,
            });
        }
    }

    // 백엔드 연결 가능한 주소인지 - 빈/무효 주소는 스테일 라우트로 취급한다
    internal static bool IsUsableBackendAddr(string? addr) =>
        !string.IsNullOrEmpty(addr) && addr.Contains(':');

    // payload 모델

    public sealed class TableSnapshot
    {
        public RouteEntry[] Entries { get; set; } = [];
    }

    public sealed class SwapPayload
    {
        public string PlayerKey { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string BackendAddr { get; set; } = "";
    }

    public sealed class KickPayload
    {
        public string PlayerKey { get; set; } = "";
        public string? Reason { get; set; }
    }

    public sealed class BanPayload
    {
        public string PlayerKey { get; set; } = "";
        public bool Banned { get; set; }
    }

    // AUTH_INFO (오케스트레이터 -> 게이트웨이 - 밴 스냅샷 + 서버 비밀번호 + 최대 인원 + 서버명)
    public sealed class AuthInfoPayload
    {
        public string? ServerPassword { get; set; }
        public string? ServerName { get; set; }
        public string[] BannedKeys { get; set; } = [];
        public int MaxPlayers { get; set; }
    }

    public sealed class MaintenancePayload
    {
        public bool On { get; set; }
        public string? Message { get; set; }
        // 락다운 bypass - 유지보수 중에도 접속 허용할 SteamID64 목록
        public ulong[]? Bypass { get; set; }
        // 켬 전환 시 현재 접속 세션 전체 추방 여부 (락다운 진입 시 true)
        public bool KickAll { get; set; }
    }

    // 로비 동적 메타데이터 (오케스트레이터가 인스턴스 리포트를 합산해 전송)
    // mod 목록은 전송하지 않는다 - EXTRADATA 와이어에는 빈 목록+false로 고정 포함
    public sealed class LobbyMetadataPayload
    {
        public int LivingCount { get; set; }
        public int HappinessSum { get; set; }
        public ulong[]? SteamIds { get; set; }
        public string? RulesBase64 { get; set; }
    }
}
