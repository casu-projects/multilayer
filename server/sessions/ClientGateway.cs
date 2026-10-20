namespace CasuMpServer;

public sealed class ClientGateway
{
    public ServerConfig Config { get; }

    private readonly Dictionary<PlayerKey, ClientSession> _sessions = new();
    private readonly HashSet<PlayerKey> _banned = new();
    private readonly object _lock = new();

    private bool _maintenance;
    private string _maintenanceMessage = "";
    private readonly HashSet<ulong> _maintenanceBypass = new();

    private bool _prewarmGate;
    private string _prewarmGateMessage = "Server is preparing layers. Please try again shortly.";

    private string _serverPassword = "";
    private int _maxPlayers = 32;
    private string _serverName = "CasuMP Server";

    private SessionLifecycle? _lifecycle;

    public ClientGateway(ServerConfig config)
    {
        Config = config;
        _serverName = string.IsNullOrEmpty(config.ServerName) ? "CasuMP Server" : config.ServerName;
        _serverPassword = config.ServerPassword;
        _maxPlayers = config.MaxPlayers > 0 ? config.MaxPlayers : 32;
    }

    public bool IsMaintenance => _maintenance;

    public string MaintenanceMessage => _maintenanceMessage;

    public bool IsAdmissionBlocked => _prewarmGate;

    public string PrewarmGateMessage => _prewarmGateMessage;

    public void SetPrewarmGate(bool blocked, string? message = null)
    {
        if (_prewarmGate == blocked) return;
        _prewarmGate = blocked;
        if (!string.IsNullOrEmpty(message)) _prewarmGateMessage = message;
        Log.Info(blocked
            ? "프리웜 게이트 닫힘 — 신규 접속 차단."
            : "프리웜 게이트 열림 — 신규 접속 허용.");
    }

    public int MaxPlayers => _maxPlayers;

    public string ServerName => _serverName;

    public bool HasPassword => !string.IsNullOrEmpty(_serverPassword);

    public int SessionCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    public (int Direct, int Steam) TransportCounts
    {
        get
        {
            lock (_lock)
            {
                int direct = 0;
                int steam = 0;
                foreach (ClientSession session in _sessions.Values)
                {
                    if (session.Transport == "Steam") steam++;
                    else direct++;
                }
                return (direct, steam);
            }
        }
    }

    public event Action? ShutdownRequested;

    internal void SetLifecycle(SessionLifecycle lifecycle) => _lifecycle = lifecycle;

    public void UpdateServerInfo(string name, string password, int maxPlayers)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(name)) _serverName = name;
            _serverPassword = password ?? "";
            _maxPlayers = maxPlayers > 0 ? maxPlayers : 32;
        }
    }

    public void SetBans(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            _banned.Clear();
            foreach (string key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key)) _banned.Add(PlayerKey.FromString(key));
            }
        }
    }

    public void SetBanned(PlayerKey key, bool banned)
    {
        ClientSession? session = null;
        lock (_lock)
        {
            if (banned)
            {
                _banned.Add(key);
                _sessions.TryGetValue(key, out session);
            }
            else
            {
                _banned.Remove(key);
            }
        }

        if (session != null)
        {
            session.Kick("You are banned from this server.");
            RemoveSession(session, "banned");
        }
    }

    public void SetMaintenance(bool on, string message, IEnumerable<ulong>? bypass, bool kickAll)
    {
        List<ClientSession> kick = new();
        int bypassCount;
        lock (_lock)
        {
            _maintenance = on;
            _maintenanceMessage = message ?? "";
            _maintenanceBypass.Clear();
            if (bypass != null)
            {
                foreach (ulong id in bypass)
                {
                    if (id != 0) _maintenanceBypass.Add(id);
                }
            }
            bypassCount = _maintenanceBypass.Count;

            if (on && kickAll)
            {
                foreach (ClientSession session in _sessions.Values.ToList())
                {
                    if (session.Disposed || IsBypassed(session)) continue;
                    kick.Add(session);
                }
            }
        }

        Log.Info($"유지보수 모드 {(on ? "켬" : "끔")} (bypass {bypassCount}명).");

        foreach (ClientSession session in kick)
        {
            session.Kick("Server entered maintenance mode");
            RemoveSession(session, "maintenance");
        }
    }

    private bool IsBypassed(ClientSession session) =>
        session.SteamId is ulong sid && _maintenanceBypass.Contains(sid);

    public bool ValidatePassword(string? candidate) =>
        string.IsNullOrEmpty(_serverPassword) || candidate == _serverPassword;

    public void AcceptSession(ClientSession session)
    {
        ClientSession? replaced = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.Player, out ClientSession? old))
            {
                _sessions.Remove(session.Player);
                replaced = old;
            }
            _sessions[session.Player] = session;
        }
        replaced?.Kick("Reconnected from a new session.");

        bool reject = false;
        string reason = "";
        lock (_lock)
        {
            if (_maintenance && !IsBypassed(session))
            {
                reject = true;
                reason = _maintenanceMessage.Length > 0 ? _maintenanceMessage : "Server is in maintenance mode.";
            }
            else if (_prewarmGate)
            {
                reject = true;
                reason = _prewarmGateMessage;
            }
            else if (_banned.Contains(session.Player))
            {
                reject = true;
                reason = "You are banned from this server.";
            }
            else if (!ValidatePassword(session.Password))
            {
                reject = true;
                reason = "Wrong password.";
            }
            else if (_sessions.Count > _maxPlayers)
            {
                reject = true;
                reason = "Server is full.";
            }
        }

        if (reject)
        {
            session.Kick(reason);
            RemoveSession(session, reason, notifyLifecycle: false);
            return;
        }

        session.EnterRoutingWait();
        _lifecycle?.OnConnected(session);
    }

    public void CloseSession(ClientSession session, string reason)
    {
        if (session.Disposed) return;
        session.Dispose();
        RemoveSession(session, reason);
    }

    public void KickSilent(ClientSession session, string reason)
    {
        if (session.Disposed) return;
        session.Kick(reason);
        RemoveSession(session, reason, notifyLifecycle: false);
    }

    public void Kick(PlayerKey key, string reason)
    {
        ClientSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(key, out session);
        }
        if (session == null) return;
        session.Kick(reason);
        RemoveSession(session, reason);
    }

    public void OnSessionBackendConnected(ClientSession session)
    {
        _lifecycle?.OnBackendConnected(session);
    }

    public void OnSessionBackendFailed(ClientSession session, string reason)
    {
        session.Kick(reason);
        RemoveSession(session, reason);
    }

    public void ApplyRoute(PlayerKey key, PlayerSessionStore.RouteInfo route)
    {
        ClientSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(key, out session);
        }

        if (session == null || !IsUsableBackendAddr(route.BackendAddr)) return;
        if (session.State == SessionState.Routing
            || (session.State == SessionState.Connecting && !session.HasEverConnectedToBackend))
        {
            session.BeginRoute(route.BackendAddr, route.ClientId, route.IsReturning, route.InstanceId);
        }
    }

    public bool Swap(PlayerKey key, string backendAddr, string? instanceId)
    {
        ClientSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(key, out session);
        }

        if (session == null || !IsUsableBackendAddr(backendAddr)) return false;
        if (session.BackendAddr == backendAddr
            && session.State is SessionState.Active or SessionState.Connecting or SessionState.Swapping)
        {
            return true;
        }

        session.SwapBackend(backendAddr, instanceId);
        return true;
    }

    public void Shutdown()
    {
        List<ClientSession> sessions;
        lock (_lock) { sessions = _sessions.Values.ToList(); }

        foreach (ClientSession session in sessions)
        {
            session.Kick("Server is shutting down.");
            RemoveSession(session, "shutdown");
        }

        Log.Info("종료 신호 수신 — 서버 종료.");
        ShutdownRequested?.Invoke();
    }

    public void Tick()
    {
        List<ClientSession> snapshot;
        lock (_lock) { snapshot = _sessions.Values.ToList(); }

        foreach (ClientSession session in snapshot)
        {
            session.Tick();
            if (session.State == SessionState.Routing
                && session.RoutingWaitStartedAt.HasValue
                && DateTime.UtcNow - session.RoutingWaitStartedAt.Value > TimeSpan.FromSeconds(Config.RoutingWaitTimeoutSeconds))
            {
                Log.Info($"{session.Username} 라우팅 대기 타임아웃 — 거부.");
                session.Kick("Server is busy, please try again.");
                RemoveSession(session, "routing timeout");
            }
        }
    }

    private void RemoveSession(ClientSession session, string reason, bool notifyLifecycle = true)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.TryGetValue(session.Player, out ClientSession? current)
                && ReferenceEquals(current, session);
            if (removed) _sessions.Remove(session.Player);
        }

        if (removed && notifyLifecycle)
        {
            _lifecycle?.OnDisconnected(session, reason);
        }
    }

    internal static bool IsUsableBackendAddr(string? addr) =>
        !string.IsNullOrEmpty(addr) && addr.Contains(':');
}
