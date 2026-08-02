using System.Text.Json;

namespace CasuMpOrchestrator;

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

    /// <summary>접속 시 표시 이름 (게이트웨이 SESSION_CONNECTED의 username — !list 등 표시용).</summary>
    public string? Username { get; set; }
}

/// <summary>플레이어 세션/라우팅 원본 (O6-3) — LayerRouter + ClientIdRegistry 통합.
/// 게이트웨이에 TABLE_SNAPSHOT/ROUTE_UPDATE 전송. 모든 접근은 메인 스레드 전용.</summary>
public sealed class PlayerSessionStore
{
    private readonly OrchestratorConfig _config;
    private readonly ControlHub _hub;
    private readonly InstanceManager _instances;

    private readonly Dictionary<PlayerKey, PlayerState> _players = new();
    private ushort _nextClientId = 1;

    private readonly string _sessionDir;
    private readonly string _clientIdPath;

    public PlayerSessionStore(OrchestratorConfig config, ControlHub hub, InstanceManager instances)
    {
        _config = config;
        _hub = hub;
        _instances = instances;
        _sessionDir = Path.Combine(config.SaveRootPath, "sessions");
        Directory.CreateDirectory(_sessionDir);
        _clientIdPath = Path.Combine(config.SaveRootPath, "client-id-counter.json");
        LoadAll();
    }

    public PlayerState? Get(PlayerKey key) => _players.TryGetValue(key, out var state) ? state : null;

    public IReadOnlyCollection<PlayerState> All => _players.Values.ToList();

    // ── 게이트웨이 세션 이벤트 ──

    /// <summary>SESSION_CONNECTED 처리 — 상태 확정 + 인스턴스 배정.
    /// ROUTE-ON-READY: 웜 인스턴스(READY/IDLE)면 즉시 라우팅, 콜드 인스턴스면
    /// INSTANCE_READY 수신 시 PushRoutesForInstance가 라우팅한다 (게이트웨이의
    /// 월드젠 거절 폴링 제거 — 2026-08-02).</summary>
    public void OnSessionConnected(PlayerKey key, string? username = null)
    {
        if (!_players.TryGetValue(key, out PlayerState? state))
        {
            state = new PlayerState { Key = key, ClientId = AllocateClientId(), Depth = 1 };
            _players[key] = state;
            Console.WriteLine($"신규 플레이어: {key} (clientId {state.ClientId})");
            SaveOne(state);
        }

        if (!string.IsNullOrEmpty(username))
        {
            state.Username = username;
        }

        state.Session = PlayerSessionState.Connecting;
        Console.WriteLine($"{key} 접속 — 레이어 {state.Depth} 배정.");

        // 인스턴스 보장 (부팅 중이면 READY 대기 — ROUTE-ON-READY)
        string? backendAddr = _instances.EnsureInstance(state.Depth);
        if (backendAddr == null)
        {
            Console.WriteLine($"{key} 인스턴스 없음 — 배정 실패.");
            _hub.Send(_hub.GatewayConnection, "KICK", new { playerKey = key.Value, reason = "Server is busy, please try again." });
            state.Session = PlayerSessionState.Offline;
            return;
        }

        var instance = _instances.FindByDepth(state.Depth);
        state.InstanceId = instance?.Key;

        if (instance != null && instance.Status is InstanceStatus.Ready or InstanceStatus.Idle)
        {
            SendRouteUpdate(state);
        }
        else
        {
            // 콜드 인스턴스 — READY 도착 시 푸시 라우팅. 게이트웨이는 Routing 대기 상태로
            // 유지되고, 오케스트레이터가 PushRoutesForInstance로 들여보낸다.
            Console.WriteLine($"{key} 접속 — {instance?.Key} 준비 대기 (ROUTE-ON-READY).");
        }
    }

    /// <summary>플레이어 1명의 ROUTE_UPDATE 전송 (웜 인스턴스 즉시 / READY 푸시 공통).</summary>
    private void SendRouteUpdate(PlayerState state)
    {
        var instance = state.InstanceId != null ? _instances.Find(state.InstanceId) : null;
        string? backendAddr = instance != null ? _instances.BackendAddrFor(instance) : null;
        if (backendAddr == null) return;

        _hub.Send(_hub.GatewayConnection, "ROUTE_UPDATE", new
        {
            playerKey = state.Key.Value,
            instanceId = instance.Key,
            clientId = state.ClientId,
            backendAddr,
            isReturning = state.IsReturning,
        });
    }

    /// <summary>ROUTE-ON-READY: 인스턴스 READY 전환 시 해당 인스턴스에 배정된 대기 세션을
    /// 일괄 라우팅한다 (INSTANCE_READY 디스패치에서 호출).</summary>
    public void PushRoutesForInstance(string instanceKey)
    {
        int pushed = 0;
        foreach (PlayerState state in _players.Values.Where(p =>
            p.InstanceId == instanceKey
            && p.Session is PlayerSessionState.Connecting or PlayerSessionState.OnLayer))
        {
            SendRouteUpdate(state);
            pushed++;
        }
        if (pushed > 0)
        {
            Console.WriteLine($"{instanceKey} READY — 대기 세션 {pushed}명 라우팅 (ROUTE-ON-READY).");
        }
    }

    public void OnBackendConnected(PlayerKey key, string instanceId)
    {
        if (!_players.TryGetValue(key, out PlayerState? state)) return;
        if (state.Session == PlayerSessionState.Migrating) return; // 마이그레이션은 코디네이터가 관리
        state.Session = PlayerSessionState.OnLayer;
        state.InstanceId = instanceId;
        Console.WriteLine($"{key} ON_LAYER ({instanceId}).");
    }

    public void OnSessionDisconnected(PlayerKey key)
    {
        if (!_players.TryGetValue(key, out PlayerState? state)) return;
        if (state.Session == PlayerSessionState.Migrating) return; // 코디네이터가 확정 처리
        state.Session = PlayerSessionState.Offline;
        Console.WriteLine($"{key} 오프라인.");
    }

    /// <summary>인스턴스 종료 — 해당 인스턴스에 배정된 Connecting(백엔드 미연결) 세션을
    /// 재배정한다 (유휴 정지/Stopping 창에 재접속한 플레이어 복구 — INSTANCE_EXITED가
    /// 도착하면 EnsureInstance가 재스폰하고 새 주소로 ROUTE_UPDATE가 나간다).</summary>
    public void OnInstanceExited(string instanceKey)
    {
        foreach (PlayerState state in _players.Values.Where(p =>
            p.InstanceId == instanceKey && p.Session == PlayerSessionState.Connecting))
        {
            Console.WriteLine($"{state.Key} 인스턴스 {instanceKey} 종료 — 재배정.");
            OnSessionConnected(state.Key);
        }
    }

    /// <summary>세션 상태 영속화 (코디네이터 등 외부 갱신 후 호출).</summary>
    public void Persist(PlayerState state) => SaveOne(state);

    /// <summary>마이그레이션 커밋 시 라우팅 갱신 (코디네이터 호출).</summary>
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
        Console.WriteLine($"{key} 마이그레이션 커밋 → 레이어 {newDepth}.");
    }

    /// <summary>리스폰 데이터 계층 (P5): 플레이어를 완전 신규 접속자 상태로 리셋 —
    /// depth=1, 라우팅 배정 해제, isReturning=false. 세이브 폐기는 PlayerDataStore.DeleteSave가
    /// 담당 (단일 소유자 규칙). keepOnline: 레이어 1 인플레이스 리스폰은 플레이어가 계속
    /// 접속 중이므로 Session/InstanceId(라우팅)를 보존한다 — 해제하면 이후 LAYER_END가
    /// "오프라인"으로 무시되어 상향 마이그레이션이 막힌다.</summary>
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
        Console.WriteLine($"{key} 리스폰 — 완전 신규 상태로 리셋 (레이어 1).");
    }

    /// <summary>게이트웨이 연결 시 전체 테이블 스냅샷 전송 (R4).
    /// ROUTE-ON-READY: READY/IDLE 인스턴스의 라우트만 미러한다 — 부팅/월드젠 중인
    /// 인스턴스는 라우팅 대상이 아니므로 스냅샷에서 제외 (게이트웨이가 월드젠 거절
    /// 폴링을 하지 않도록).</summary>
    public void PushTableSnapshot()
    {
        var entries = _players.Values
            .Select(state =>
            {
                var instance = state.InstanceId != null ? _instances.Find(state.InstanceId) : null;
                string? backendAddr = instance != null
                    && instance.Status is InstanceStatus.Ready or InstanceStatus.Idle
                    ? _instances.BackendAddrFor(instance)
                    : null;
                return new
                {
                    playerKey = state.Key.Value,
                    instanceId = state.InstanceId,
                    clientId = state.ClientId,
                    backendAddr = backendAddr ?? "",
                    isReturning = state.IsReturning,
                };
            })
            .Where(e => e.backendAddr.Length > 0)
            .ToArray();
        _hub.SendNoAck(_hub.GatewayConnection, "TABLE_SNAPSHOT", new { entries });
        Console.WriteLine($"게이트웨이에 테이블 스냅샷 전송 ({entries.Length}명).");
    }

    // ── 영속화 ──

    private ushort AllocateClientId()
    {
        while (_players.Values.Any(p => p.ClientId == _nextClientId))
        {
            _nextClientId = (ushort)((_nextClientId + 1) % ushort.MaxValue);
            if (_nextClientId == 0) _nextClientId = 1;
        }
        File.WriteAllText(_clientIdPath, JsonSerializer.Serialize(new { next = _nextClientId }));
        return _nextClientId;
    }

    private void SaveOne(PlayerState state)
    {
        string dir = Path.Combine(_sessionDir, Sanitize(state.Key.Value));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(new
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
        Console.WriteLine($"복원: {_players.Count}명");
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
