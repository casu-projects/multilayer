namespace CasuMpOrchestrator;

public enum InstanceStatus
{
    Stopped,
    Starting,   // SPAWN 명령 발신됨 (프로세스 기동 대기)
    Booting,    // 모드 연결됨 (월드 생성 중)
    Ready,      // INSTANCE_READY — 월드+세이브 로드 완료
    Idle,       // 플레이어 0, 유휴 타이머 진행
    Stopping,
    Crashed,
}

public sealed class InstanceInfo
{
    public required string Key { get; init; }   // "depth-1"
    public required int Depth { get; init; }
    public int Port { get; set; }
    public string? MachineId { get; set; }
    public InstanceStatus Status { get; set; } = InstanceStatus.Stopped;
    public DateTime LastNonIdleAt { get; set; } = DateTime.UtcNow;
    public int ConnectedCount { get; set; }
    public ControlHub.ClientConnection? ModConnection { get; set; }
}

/// <summary>인스턴스 수명주기 — 노드 에이전트 경유 스폰/정지, READY 검증 (O6-2, O2).
/// 모든 접근은 메인 스레드 전용.</summary>
public sealed class InstanceManager
{
    private readonly OrchestratorConfig _config;
    private readonly ControlHub _hub;

    private readonly Dictionary<string, InstanceInfo> _instances = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly Dictionary<string, (int Used, int Capacity, string Address)> _agents = new();

    public InstanceManager(OrchestratorConfig config, ControlHub hub)
    {
        _config = config;
        _hub = hub;
    }

    public IReadOnlyCollection<InstanceInfo> All => _instances.Values.ToList();

    public InstanceInfo? Find(string key) => _instances.TryGetValue(key, out var info) ? info : null;

    public InstanceInfo? FindByDepth(int depth) => _instances.Values.FirstOrDefault(i => i.Depth == depth);

    // ── 에이전트 등록 (AGENT_HELLO) ──

    public void RegisterAgent(string machineId, int capacity, string address)
    {
        _agents[machineId] = (0, capacity, address);
        Console.WriteLine($"에이전트 등록: {machineId} (수용 {capacity}, 주소 {address})");
    }

    public void UnregisterAgent(string machineId)
    {
        _agents.Remove(machineId);
        foreach (InstanceInfo info in _instances.Values.Where(i => i.MachineId == machineId))
        {
            info.Status = InstanceStatus.Crashed;
            Console.WriteLine($"에이전트 {machineId} 이탈 — 인스턴스 {info.Key} 크래시 처리");
        }
        Console.WriteLine($"에이전트 등록 해제: {machineId}");
    }

    public string? BackendAddrFor(InstanceInfo info)
    {
        if (info.MachineId != null && _agents.TryGetValue(info.MachineId, out var agent))
            return $"{agent.Address}:{info.Port}";
        // 스폰 없이 재수용된 인스턴스: 모드 연결의 원격 IP 사용
        if (info.ModConnection != null
            && info.ModConnection.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep)
        {
            return $"{ep.Address}:{info.Port}";
        }
        return null;
    }

    // ── 스폰 (G-2 파라미터 세트) ──

    /// <summary>해당 depth의 인스턴스를 보장 (없으면 SPAWN). 백엔드 주소 반환 — 부팅 중이어도
    /// 게이트웨이가 백엔드 재시도로 커버 (R2). Stopped/Crashed 기록은 죽은 프로세스이므로
    /// 재스폰한다 (유휴 정지 후 재접속 시 고아 주소로 라우팅되는 것 방지).</summary>
    public string? EnsureInstance(int depth)
    {
        InstanceInfo? info = FindByDepth(depth);
        if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            if (info != null)
            {
                Console.WriteLine($"{info.Key} 재스폰 (상태 {info.Status}).");
            }
            info = Spawn(depth);
            if (info == null) return null;
        }
        return BackendAddrFor(info);
    }

    private InstanceInfo? Spawn(int depth)
    {
        if (_agents.Count == 0)
        {
            Console.WriteLine($"에이전트가 없어 depth-{depth} 스폰 불가.");
            return null;
        }

        // 배치: 수용량 대비 사용량이 가장 적은 에이전트
        string? machineId = _agents
            .Where(kv => kv.Value.Used < kv.Value.Capacity)
            .OrderBy(kv => (double)kv.Value.Used / kv.Value.Capacity)
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (machineId == null)
        {
            Console.WriteLine($"모든 에이전트 수용량 초과 — depth-{depth} 스폰 불가.");
            return null;
        }

        int port = AllocatePort();
        string key = DepthKey(depth);
        var info = new InstanceInfo { Key = key, Depth = depth, Port = port, MachineId = machineId, Status = InstanceStatus.Starting };
        _instances[key] = info;
        _runNeeded.Add(key); // 모든 인스턴스는 첫 플레이어 연결 시 START_RUN 필요 (G-1)

        var agent = _agents[machineId];
        _agents[machineId] = (agent.Used + 1, agent.Capacity, agent.Address);

        ControlHub.ClientConnection? conn = _hub.AgentConnection(machineId);
        bool sent = _hub.Send(conn, "SPAWN", new
        {
            instanceKey = key,
            depth,
            port,
            serverName = _config.ServerName,
            serverPassword = _config.ServerPassword,
        }, (ok, reason) =>
        {
            if (!ok)
            {
                Console.WriteLine($"depth-{depth} SPAWN 실패: {reason}");
                info.Status = InstanceStatus.Crashed;
            }
        });

        Console.WriteLine($"depth-{depth} 스폰 요청 → {machineId} (포트 {port})");
        return info;
    }

    private int AllocatePort()
    {
        for (int p = _config.InstancePortStart; p < _config.InstancePortStart + _config.InstancePortRange; p++)
        {
            if (_usedPorts.Contains(p)) continue;
            _usedPorts.Add(p);
            return p;
        }
        throw new InvalidOperationException("인스턴스 포트 고갈");
    }

    public void StopInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        info.Status = InstanceStatus.Stopping;
        _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), "STOP", new { instanceKey = key });
        Console.WriteLine($"{key} 정지 요청.");
    }

    /// <summary>런 시작 대기 표시 (G-1 개정) — 스폰 시점과 모드 연결(MOD_HELLO) 시점에
    /// 표시되고, MOD_HELLO에서 바로 START_RUN을 전송한다. 인스턴스 수명당 1회 전송.
    /// (프리웜: 월드젠은 플레이어 접속과 무관하게 인스턴스 부팅 직후 시작)</summary>
    private readonly HashSet<string> _runNeeded = new();

    public void MarkRunNeeded(string? key)
    {
        if (key == null) return;
        // 이미 실행 중(READY/IDLE)인 인스턴스는 런 시작 불필요 (인스턴스 수명당 1회).
        InstanceInfo? info = Find(key);
        if (info != null && info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            return;
        _runNeeded.Add(key);
    }

    /// <summary>대기 중이던 START_RUN 전송 (인스턴스 수명당 1회).</summary>
    public void TrySendStartRun(string key)
    {
        if (!_runNeeded.Remove(key)) return;
        _hub.Send(_hub.ModConnection(key), "START_RUN", new { instanceKey = key });
    }

    // ── 모드/에이전트 이벤트 ──

    public void OnModHello(ControlHub.ClientConnection conn, ModHelloPayload payload)
    {
        string key = DepthKey(payload.Depth);
        InstanceInfo? info = Find(key);
        if (info == null)
        {
            // 오케스트레이터 재시작 후 기존 인스턴스 재수용
            info = new InstanceInfo { Key = key, Depth = payload.Depth, Port = payload.Port, Status = InstanceStatus.Booting };
            _instances[key] = info;
            _usedPorts.Add(payload.Port);
            Console.WriteLine($"{key} 재수용 (기존 인스턴스).");
        }
        info.Port = payload.Port;
        info.ModConnection = conn;
        if (info.Status is InstanceStatus.Starting or InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info.Status = InstanceStatus.Booting;
        }
        Console.WriteLine($"{key} 모드 연결 (포트 {payload.Port}).");

        // 프리웜 (G-1 개정): START_RUN은 첫 플레이어 백엔드 연결이 아니라 모드 연결 시점에 발신.
        // 월드젠이 플레이어 없이 진행되어, 접속/마이그레이션이 목적지 READY 이후에만 이뤄진다
        // (콜드 인스턴스의 announce 스테일 시드/월드젠 중 접속 거절 문제를 구조적으로 제거).
        // 재수용/재시작에도 안전 — 모드 측 HandleStartRun이 이미 생성된 세계면 무시한다.
        _runNeeded.Add(key);
        TrySendStartRun(key);
    }

    /// <summary>인스턴스 READY 전이 (Booting/Starting → Ready). 실제 전이된 경우에만 true —
    /// 중복 INSTANCE_READY 보고(모드 재보고 등)는 false를 반환해 호출부가
    /// ROUTE-ON-READY 푸시를 1회만 수행하도록 한다 (2026-08-02 중복 푸시 회귀 수정).</summary>
    public bool OnInstanceReady(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        // 정지/크래시 중인 인스턴스는 READY로 부활시키지 않는다 (중복 정지 방지)
        if (info.Status != InstanceStatus.Booting && info.Status != InstanceStatus.Starting)
            return false;
        info.Status = InstanceStatus.Ready;
        return true;
    }

    public void OnInstanceFault(string key, string reason)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        Console.WriteLine($"{key} FAULT: {reason}");
        info.Status = InstanceStatus.Crashed;
        info.ModConnection = null;
    }

    public void OnModConnectionClosed(ControlHub.ClientConnection conn)
    {
        InstanceInfo? info = _instances.Values.FirstOrDefault(i => i.ModConnection == conn);
        if (info == null) return;
        info.ModConnection = null;
        if (info.Status is InstanceStatus.Booting or InstanceStatus.Ready or InstanceStatus.Idle)
        {
            Console.WriteLine($"{info.Key} 모드 연결 종료 — 크래시 처리.");
            info.Status = InstanceStatus.Crashed;
        }
    }

    public void OnAgentConnectionClosed(ControlHub.ClientConnection conn)
    {
        if (conn.MachineId != null)
        {
            UnregisterAgent(conn.MachineId);
        }
    }

    public void OnInstanceExited(string key, int code)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        _usedPorts.Remove(info.Port);
        if (info.MachineId != null && _agents.TryGetValue(info.MachineId, out var agent))
        {
            _agents[info.MachineId] = (Math.Max(0, agent.Used - 1), agent.Capacity, agent.Address);
        }
        info.Status = InstanceStatus.Stopped;
        info.ModConnection = null;
        Console.WriteLine($"{key} 종료 (code {code}).");
    }

    // ── 유휴 정리 (O2 인스턴스 수명주기) ──

    public void TickIdle(DateTime now, Func<string, int> connectedCountForInstance)
    {
        foreach (InstanceInfo info in _instances.Values.ToList())
        {
            // Starting/Booting은 유휴 판정 대상 아님 (부팅 중 — READY 대기)
            if (info.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
                continue;

            info.ConnectedCount = connectedCountForInstance(info.Key);
            if (info.ConnectedCount > 0)
            {
                info.LastNonIdleAt = now;
                info.Status = InstanceStatus.Ready;
                continue;
            }

            if (now - info.LastNonIdleAt < TimeSpan.FromSeconds(_config.IdleTeardownGraceSeconds))
            {
                if (info.Status == InstanceStatus.Ready)
                    info.Status = InstanceStatus.Idle;
                continue;
            }

            Console.WriteLine($"{info.Key} 유휴 초과 — 정지.");
            StopInstance(info.Key);
        }
    }

    public static string DepthKey(int depth) => $"depth-{depth}";
}

public sealed class ModHelloPayload
{
    public string InstanceKey { get; set; } = "";
    public int Port { get; set; }
    public int Depth { get; set; }
}
