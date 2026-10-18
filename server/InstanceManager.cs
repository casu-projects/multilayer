using CasuMp.Protocol;
namespace CasuMpServer;

public enum InstanceStatus
{
    Stopped,
    Starting,   // SPAWN 명령 발신됨 (프로세스 기동 대기)
    Booting,    // 모드 연결됨 (월드 생성 중)
    Ready,      // INSTANCE_READY - 월드+세이브 로드 완료
    Idle,       // 플레이어 0, 유휴 타이머 진행
    IdleReset,  // 유휴 유예 경과 - Prewarm 인스턴스 레이어 초기화(RESET) 발신됨
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
    public bool HasPlayedSinceReset { get; set; }
}

public sealed class InstanceManager
{
    private const int PrewarmDepth = 1;

    private static readonly TimeSpan PrewarmIdleShutdownDelay = TimeSpan.FromHours(1);

    private readonly ServerConfig _config;
    private readonly ControlHub _hub;

    private readonly Dictionary<string, InstanceInfo> _instances = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly AgentRegistry _agents = new();

    public InstanceManager(ServerConfig config, ControlHub hub)
    {
        _config = config;
        _hub = hub;
    }

    public IReadOnlyCollection<InstanceInfo> All => _instances.Values.ToList();

    public bool PrewarmIdleShutdownEnabled
    {
        get => _config.PrewarmIdleShutdown;
        set => _config.PrewarmIdleShutdown = value;
    }

    public InstanceInfo? Find(string key) => _instances.TryGetValue(key, out var info) ? info : null;

    public InstanceInfo? FindByDepth(int depth) => _instances.Values.FirstOrDefault(i => i.Depth == depth);

    public void RegisterAgent(string machineId, int capacity, string address)
    {
        _agents.Register(machineId, capacity, address);
    }

    public void UnregisterAgent(string machineId)
    {
        _agents.Unregister(machineId);
        foreach (InstanceInfo info in _instances.Values.Where(i => i.MachineId == machineId))
        {
            info.Status = InstanceStatus.Crashed;
            Log.Info($"에이전트 {machineId} 이탈 — 인스턴스 {info.Key} 크래시 처리");
        }
    }

    public string? BackendAddrFor(InstanceInfo info)
    {
        if (info.MachineId != null && _agents.TryGetAddress(info.MachineId, out string address))
            return $"{address}:{info.Port}";
        if (info.ModConnection != null
            && info.ModConnection.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep)
        {
            return $"{ep.Address}:{info.Port}";
        }
        return null;
    }

    public string? EnsureInstance(int depth)
    {
        InstanceInfo? info = FindByDepth(depth);
        if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info = Spawn(depth);
            if (info == null) return null;
        }
        return BackendAddrFor(info);
    }

    private InstanceInfo? Spawn(int depth)
    {
        if (_agents.IsEmpty)
        {
            Log.Info($"에이전트가 없어 depth-{depth} 스폰 불가.");
            return null;
        }

        string? machineId = SelectMachine();
        if (machineId == null)
        {
            Log.Info($"모든 에이전트 수용량 초과 — depth-{depth} 스폰 불가.");
            return null;
        }

        int port = AllocatePort();
        string key = DepthKey(depth);
        var info = new InstanceInfo { Key = key, Depth = depth, Port = port, MachineId = machineId, Status = InstanceStatus.Starting };
        _instances[key] = info;
        _runNeeded.Add(key); // 모든 인스턴스는 첫 플레이어 연결 시 START_RUN 필요 (G-1)

        _agents.IncrementUsage(machineId);

        ControlHub.ClientConnection? conn = _hub.AgentConnection(machineId);
        bool sent = _hub.Send(conn, MessageTypes.Spawn, new
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
                Log.Info($"depth-{depth} SPAWN 실패: {reason}");
                info.Status = InstanceStatus.Crashed;
            }
        });

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

    private string? SelectMachine()
    {
        return _agents.Select();
    }

    public void StopInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        info.Status = InstanceStatus.Stopping;
        _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), MessageTypes.Stop, new { instanceKey = key });
    }

    public void ResetInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        if (info.ModConnection == null)
        {
            Log.Info($"{key} RESET 불가 — 모드 미연결. 크래시 처리.");
            info.Status = InstanceStatus.Crashed;
            return;
        }
        info.Status = InstanceStatus.IdleReset;
        _hub.Send(info.ModConnection, MessageTypes.Reset, new { instanceKey = key }, (ok, reason) =>
        {
            if (!ok)
            {
                Log.Info($"{key} RESET 실패: {reason} — 크래시 처리.");
                info.Status = InstanceStatus.Crashed;
            }
        });
    }

    public void PrewarmLayers()
    {
        int depth = PrewarmDepth;
        InstanceInfo? existing = FindByDepth(depth);
        if (existing != null && existing.Status is InstanceStatus.Ready or InstanceStatus.Idle)
        {
            return; // 이미 준비됨
        }
        if (existing != null && existing.Status is InstanceStatus.Starting or InstanceStatus.Booting or InstanceStatus.IdleReset)
        {
            if (_prewarmInFlight < 0) _prewarmInFlight = depth;
            return;
        }
        if (!_prewarmQueue.Contains(depth) && depth != _prewarmInFlight)
        {
            _prewarmQueue.Enqueue(depth);
        }
    }

    private readonly Queue<int> _prewarmQueue = new();
    private int _prewarmInFlight = -1;

    public void TickPrewarm()
    {
        if (_prewarmInFlight >= 0)
        {
            InstanceInfo? info = FindByDepth(_prewarmInFlight);
        if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed) // 없거나 죽었으면 재스폰
            {
                _prewarmInFlight = -1; // 실패 - 다음 항목으로
            }
            else if (info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            {
                _prewarmInFlight = -1; // 완료 - 다음 항목으로
            }
            else
            {
                return; // 아직 부팅/월드젠 중 - 대기
            }
        }

        while (_prewarmQueue.Count > 0)
        {
            int depth = _prewarmQueue.Dequeue();
            InstanceInfo? existing = FindByDepth(depth);
            if (existing != null && existing.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            {
                continue; // 사이에 준비됨
            }
            if (existing != null && existing.Status is InstanceStatus.Starting or InstanceStatus.Booting or InstanceStatus.IdleReset)
            {
                _prewarmInFlight = depth; // 이미 진행 중 - 대기
                return;
            }
            string? addr = EnsureInstance(depth);
            if (addr != null)
            {
                _prewarmInFlight = depth;
                return; // 스폰됨 - READY까지 대기
            }
        }
    }

    private readonly HashSet<string> _runNeeded = new();

    public void MarkRunNeeded(string? key)
    {
        if (key == null) return;
        InstanceInfo? info = Find(key);
        if (info != null && info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            return;
        _runNeeded.Add(key);
    }

    public void TrySendStartRun(string key)
    {
        if (!_runNeeded.Remove(key)) return;
        _hub.Send(_hub.ModConnection(key), MessageTypes.StartRun, new { instanceKey = key });
    }

    public void OnModHello(ControlHub.ClientConnection conn, ModHelloPayload payload)
    {
        string key = DepthKey(payload.Depth);
        InstanceInfo? info = Find(key);
        if (info == null)
        {
            info = new InstanceInfo { Key = key, Depth = payload.Depth, Port = payload.Port, Status = InstanceStatus.Booting };
            _instances[key] = info;
            _usedPorts.Add(payload.Port);
        }
        info.Port = payload.Port;
        info.ModConnection = conn;
        if (info.Status is InstanceStatus.Starting or InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info.Status = InstanceStatus.Booting;
        }
        _runNeeded.Add(key);
        TrySendStartRun(key);
    }

    public bool OnInstanceReady(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (info.Status != InstanceStatus.Booting && info.Status != InstanceStatus.Starting
            && info.Status != InstanceStatus.IdleReset)
            return false;
        info.Status = InstanceStatus.Ready; // READY 전이
        info.LastNonIdleAt = DateTime.UtcNow;
        return true;
    }

    public void OnInstanceFault(string key, string reason)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        Log.Info($"{key} FAULT: {reason}");
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
            Log.Info($"{info.Key} 모드 연결 종료 — 크래시 처리.");
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
        if (info.MachineId != null)
        {
            _agents.DecrementUsage(info.MachineId);
        }
        info.Status = InstanceStatus.Stopped; // 종료
        info.ModConnection = null;
    }

    public void TickIdle(DateTime now, Func<string, int> connectedCountForInstance)
    {
        foreach (InstanceInfo info in _instances.Values.ToList())
        {
            if (info.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
                continue;

            info.ConnectedCount = connectedCountForInstance(info.Key);
            if (info.ConnectedCount > 0)
            {
                info.LastNonIdleAt = now;
                info.Status = InstanceStatus.Ready;
                info.HasPlayedSinceReset = true;
                continue;
            }

            if (now - info.LastNonIdleAt < TimeSpan.FromSeconds(_config.IdleTeardownGraceSeconds))
            {
                if (info.Status == InstanceStatus.Ready)
                    info.Status = InstanceStatus.Idle;
                continue;
            }

            bool isPrewarm = info.Depth == PrewarmDepth;
            if (isPrewarm)
            {
                if (PrewarmIdleShutdownEnabled && now - info.LastNonIdleAt >= PrewarmIdleShutdownDelay)
                {
                    Log.Info($"{info.Key} 유휴 초과(1시간) — 정지.");
                    StopInstance(info.Key);
                    continue;
                }

                if (!info.HasPlayedSinceReset)
                {
                    info.Status = InstanceStatus.Idle;
                    continue;
                }

                info.HasPlayedSinceReset = false;
                ResetInstance(info.Key);
                continue;
            }

            Log.Info($"{info.Key} 유휴 초과 — 정지.");
            StopInstance(info.Key);
        }
    }

    public static string DepthKey(int depth) => $"depth-{depth}";
}
