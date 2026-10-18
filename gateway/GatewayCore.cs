using System.Collections.Concurrent;
using System.Text.Json;

namespace CasuMpGateway;

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

    public bool AuthInfoReceived { get; private set; }

    public bool ValidatePassword(string? candidate) =>
        string.IsNullOrEmpty(_serverPassword) || candidate == _serverPassword;

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

    public int SessionCount => _sessions.Count;

    public Action<LobbyMetadataPayload>? OnLobbyMetadata { get; set; }

    public void EnqueueCommand(ControlMessage msg) => _inbound.Enqueue(msg);

    public ControlMessage? TryDequeueOutbound() => _outbound.TryDequeue(out var msg) ? msg : null;

    public long NextSeq() => ++_seqCounter;

    public void Report(string type, object? payload) =>
        _outbound.Enqueue(ControlMessage.Create(NextSeq(), type, payload));

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

    public void AcceptSession(ClientSession session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.Player, out ClientSession? old))
            {
                _sessions.Remove(session.Player);
                old.Kick("Reconnected from a new session."); // 중복 세션 폐기
            }
            _sessions[session.Player] = session;
        }

        Report("SESSION_CONNECTED", new
        {
            playerKey = session.Player.Value,
            steamId = session.SteamId,
            username = session.Username,
        });

        if (_maintenance && !IsBypassed(session)) // 점검 모드
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
            session.EnterRoutingWait(); // 라우팅 확정 전 대기
        }
    }

    public void CloseSession(ClientSession session, string reason)
    {
        if (session.Disposed) return;
        session.Dispose();
        RemoveSession(session, reason);
    }
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
                case "SHUTDOWN":
                    ApplyShutdown();
                    break;
                case "LOBBY_METADATA":
                    ApplyLobbyMetadata(msg);
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
                return;
            }
        }
        if (session.BackendAddr == payload.BackendAddr
            && session.State is SessionState.Active or SessionState.Connecting or SessionState.Swapping)
        {
            return;
        }
        session.SwapBackend(payload.BackendAddr, payload.InstanceId); // 클라 연결 유지, 백엔드 교체
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

    private bool IsBypassed(ClientSession session) =>
        session.SteamId is ulong sid && _maintenanceBypass.Contains(sid);

    private void ApplyLobbyMetadata(ControlMessage msg)
    {
        var payload = msg.PayloadAs<LobbyMetadataPayload>() ?? throw new InvalidDataException("payload 없음");
        _lastLobbyMetadata = payload;
        OnLobbyMetadata?.Invoke(payload);
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

    internal static bool IsUsableBackendAddr(string? addr) =>
        !string.IsNullOrEmpty(addr) && addr.Contains(':');

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
        public ulong[]? Bypass { get; set; }
        public bool KickAll { get; set; }
    }

    public sealed class LobbyMetadataPayload
    {
        public int LivingCount { get; set; }
        public int HappinessSum { get; set; }
        public ulong[]? SteamIds { get; set; }
        public string? RulesBase64 { get; set; }
    }
}
