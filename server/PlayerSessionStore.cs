using CasuMp.Protocol;
using System.Text.Json;

namespace CasuMpServer;

public enum PlayerSessionState
{
    Offline,
    Connecting,
    OnLayer,
    Migrating,
}

public sealed class PlayerState
{
    public required PlayerKey Key { get; init; }
    public ushort ClientId { get; set; }
    public int Depth { get; set; } = 1;
    public string? InstanceId { get; set; }
    public PlayerSessionState Session { get; set; } = PlayerSessionState.Offline;
    public bool IsReturning { get; set; }

    public string? Username { get; set; }
}

public sealed class PlayerSessionStore
{
    private readonly ServerConfig _config;
    private readonly InstanceManager _instances;

    private readonly Dictionary<PlayerKey, PlayerState> _players = new();
    private ushort _nextClientId = 1;

    private readonly string _sessionDir;
    private readonly string _clientIdPath;

    public event Action<PlayerKey, RouteInfo>? RouteReady;
    public event Action<PlayerKey, string>? KickRequested;

    public PlayerSessionStore(ServerConfig config, InstanceManager instances)
    {
        _config = config;
        _instances = instances;
        _sessionDir = Path.Combine(config.SaveRootPath, "sessions");
        Directory.CreateDirectory(_sessionDir);
        _clientIdPath = Path.Combine(config.SaveRootPath, "client-id-counter.json");
        LoadAll();
    }

    public PlayerState? Get(PlayerKey key) => _players.TryGetValue(key, out var state) ? state : null;

    public IReadOnlyCollection<PlayerState> All => _players.Values.ToList();

    public bool OnSessionConnected(PlayerKey key, string? username = null)
    {
        if (!_players.TryGetValue(key, out PlayerState? state))
        {
            state = new PlayerState { Key = key, ClientId = AllocateClientId(), Depth = 1 };
            _players[key] = state;
            Log.Info($"신규 플레이어: {key} (clientId {state.ClientId})");
            SaveOne(state);
        }

        if (!string.IsNullOrEmpty(username))
        {
            state.Username = username;
        }

        state.Session = PlayerSessionState.Connecting;
        Log.Info($"{key} 접속 — 레이어 {state.Depth} 배정.");

        string? backendAddr = _instances.EnsureInstance(state.Depth);
        if (backendAddr == null)
        {
            Log.Info($"{key} 인스턴스 없음 — 배정 실패.");
            state.Session = PlayerSessionState.Offline;
            return false;
        }

        var instance = _instances.FindByDepth(state.Depth);
        state.InstanceId = instance?.Key;

        if (instance != null && instance.Status is InstanceStatus.Ready or InstanceStatus.Idle)
        {
            SendRouteUpdate(state);
        }
        else
        {
            Log.Info($"{key} 접속 — {instance?.Key} 준비 대기 (ROUTE-ON-READY).");
        }
        return true;
    }

    private void SendRouteUpdate(PlayerState state)
    {
        var instance = state.InstanceId != null ? _instances.Find(state.InstanceId) : null;
        string? backendAddr = instance != null ? _instances.BackendAddrFor(instance) : null;
        if (backendAddr == null) return;

        RouteReady?.Invoke(state.Key, new RouteInfo(instance!.Key, state.ClientId, backendAddr, state.IsReturning));
    }

    public void PushRoutesForInstance(string instanceKey)
    {
        foreach (PlayerState state in _players.Values.Where(p =>
            p.InstanceId == instanceKey
            && p.Session is PlayerSessionState.Connecting or PlayerSessionState.OnLayer).ToList())
        {
            SendRouteUpdate(state);
        }
    }

    public void OnBackendConnected(PlayerKey key, string instanceId)
    {
        if (!_players.TryGetValue(key, out PlayerState? state)) return;
        if (state.Session == PlayerSessionState.Migrating) return; // 마이그레이션은 코디네이터가 관리
        state.Session = PlayerSessionState.OnLayer;
        state.InstanceId = instanceId;
        Log.Info($"{key} ON_LAYER ({instanceId}).");
    }

    public void OnSessionDisconnected(PlayerKey key)
    {
        if (!_players.TryGetValue(key, out PlayerState? state)) return;
        if (state.Session == PlayerSessionState.Migrating) return; // 코디네이터가 확정 처리
        state.Session = PlayerSessionState.Offline;
        Log.Info($"{key} 오프라인.");
    }

    public void OnInstanceExited(string instanceKey)
    {
        foreach (PlayerState state in _players.Values.Where(p =>
            p.InstanceId == instanceKey && p.Session == PlayerSessionState.Connecting).ToList())
        {
            if (!OnSessionConnected(state.Key))
            {
                KickRequested?.Invoke(state.Key, "Server is busy, please try again.");
            }
        }
    }

    public void Persist(PlayerState state) => SaveOne(state);

    public void CommitMigration(PlayerKey key, int newDepth, string? instanceId)
    {
        if (!_players.TryGetValue(key, out PlayerState? state))
        {
            state = new PlayerState { Key = key, ClientId = AllocateClientId(), Depth = newDepth };
            _players[key] = state;
        }
        state.Depth = newDepth;
        state.InstanceId = instanceId;
        state.Session = PlayerSessionState.OnLayer;
        state.IsReturning = true;
        SaveOne(state);
        Log.Info($"{key} 마이그레이션 커밋 → 레이어 {newDepth}.");
    }

    public void PersistAllOffline()
    {
        foreach (PlayerState state in _players.Values)
        {
            state.Session = PlayerSessionState.Offline;
            SaveOne(state);
        }
        Log.Info($"전 세션 Offline 영속화 ({_players.Count}명).");
    }

    public void ResetToFresh(PlayerKey key, bool keepOnline = false)
    {
        if (!_players.TryGetValue(key, out PlayerState? state))
        {
            state = new PlayerState { Key = key, ClientId = AllocateClientId(), Depth = 1 };
            _players[key] = state;
        }
        state.Depth = 1;
        if (!keepOnline)
        {
            state.InstanceId = null;
            state.Session = PlayerSessionState.Offline;
        }
        state.IsReturning = false;
        SaveOne(state);
    }

    public readonly record struct RouteInfo(string? InstanceId, ushort ClientId, string BackendAddr, bool IsReturning);

    private ushort AllocateClientId()
    {
        while (_players.Values.Any(p => p.ClientId == _nextClientId)) // clientId 재사용 방지
        {
            _nextClientId = (ushort)((_nextClientId + 1) % ushort.MaxValue);
            if (_nextClientId == 0) _nextClientId = 1;
        }
        AtomicJsonFile.Write(_clientIdPath, JsonSerializer.Serialize(new { next = _nextClientId }));
        return _nextClientId;
    }

    private void SaveOne(PlayerState state)
    {
        string dir = Path.Combine(_sessionDir, Sanitize(state.Key.Value));
        AtomicJsonFile.Write(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(new
        {
            state.ClientId,
            state.Depth,
            state.IsReturning,
            state.Username,
        }));
    }

    private void LoadAll()
    {
        try
        {
            if (File.Exists(_clientIdPath))
            {
                var counter = JsonSerializer.Deserialize<ClientIdCounter>(File.ReadAllText(_clientIdPath));
                _nextClientId = counter?.Next ?? 1;
            }
        }
        catch { }

        if (!Directory.Exists(_sessionDir)) return;
        foreach (string dir in Directory.GetDirectories(_sessionDir))
        {
            string file = Path.Combine(dir, "session.json");
            if (!File.Exists(file)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<PlayerStateDto>(File.ReadAllText(file));
                if (dto == null) continue;
                var key = PlayerKey.FromString(Path.GetFileName(dir));
                _players[key] = new PlayerState
                {
                    Key = key,
                    ClientId = dto.ClientId,
                    Depth = dto.Depth,
                    IsReturning = dto.IsReturning,
                    Username = dto.Username,
                };
            }
            catch { }
        }
        Log.Info($"복원: {_players.Count}명");
    }

    private static string Sanitize(string s)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Create(s.Length, (s, invalid), (span, state) =>
        {
            for (int i = 0; i < state.s.Length; i++)
                span[i] = state.invalid.Contains(state.s[i]) ? '_' : state.s[i];
        });
    }

    private sealed class PlayerStateDto
    {
        public ushort ClientId { get; set; }
        public int Depth { get; set; }
        public bool IsReturning { get; set; }
        public string? Username { get; set; }
    }

    private sealed class ClientIdCounter
    {
        public ushort Next { get; set; }
    }
}
