using CasuMp.Protocol;
using System.Collections.Concurrent;

namespace CasuMpServer;

public enum InstanceStatus
{
    Stopped,
    Starting,
    Booting,
    Ready,
    Idle,
    IdleReset,
    Suspending,
    Suspended,
    Resuming,
    Stopping,
    Crashed,
}

public sealed class InstanceInfo
{
    public required string Key { get; init; }
    public required int Depth { get; init; }
    public int Port { get; set; }
    public string Generation { get; set; } = "";
    public string? MachineId { get; set; }
    public volatile InstanceStatus Status = InstanceStatus.Stopped;
    public DateTime LastNonIdleAt { get; set; } = DateTime.UtcNow;
    public int ConnectedCount { get; set; }
    public volatile ControlHub.ClientConnection? ModConnection;
    public bool HasPlayedSinceReset { get; set; }
    public volatile bool WorldReady;
    public bool SuspendAfterReset { get; set; }
    public string SuspendReasonAfterReset { get; set; } = "reset";
}

public sealed class InstanceManager
{
    public const int LayerCount = 5;

    private readonly ServerConfig _config;
    private readonly ControlHub _hub;

    private readonly ConcurrentDictionary<string, InstanceInfo> _instances = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly AgentRegistry _agents = new();

    private readonly Queue<int> _prewarmQueue = new();
    private readonly HashSet<int> _prewarmQueued = new();
    private int _prewarmInFlight = -1;
    private DateTime _prewarmWaitStartedUtc = DateTime.MinValue;
    private DateTime _prewarmRetryAt = DateTime.MinValue;
    private DateTime _prewarmNoticeNextAt = DateTime.MinValue;
    private string _prewarmNotice = "";
    private bool _prewarmCompletedOnce;
    private bool _admissionReady;
    private bool _admissionEverOpened;

    private static readonly TimeSpan PrewarmNoticeInterval = TimeSpan.FromSeconds(60);

    private readonly HashSet<string> _seenMachines = new();
    private readonly HashSet<string> _cleanedMachines = new();
    private readonly HashSet<string> _cleanupPending = new();
    private readonly Dictionary<string, DateTime> _cleanupRetryAt = new();

    private readonly HashSet<int> _swapDepths = new();
    private SwapRequest? _swap;

    private enum SwapPhase
    {
        Stopping,
        WaitingReady,
        RollbackStopping,
    }

    private sealed class SwapRequest
    {
        public required int DepthA { get; init; }
        public required int DepthB { get; init; }
        public required string MachineA { get; init; }
        public required string MachineB { get; init; }
        public required bool SuspendA { get; init; }
        public required bool SuspendB { get; init; }
        public SwapPhase Phase { get; set; } = SwapPhase.Stopping;
        public DateTime Deadline { get; set; }
    }

    public InstanceManager(ServerConfig config, ControlHub hub)
    {
        _config = config;
        _hub = hub;
    }

    public IReadOnlyCollection<InstanceInfo> All => _instances.Values.ToList();

    public event Action<string>? InstanceResumed;
    public event Action<string, string>? InstanceSuspended;

    public bool AdmissionReady => _admissionReady;
    public bool AdmissionEverOpened => _admissionEverOpened;

    public int CleanedAgentCapacity =>
        _agents.TotalCapacity(IsCleanedMachine);

    public bool PrewarmComplete
    {
        get
        {
            if (_prewarmInFlight >= 0) return false;
            for (int depth = 1; depth <= LayerCount; depth++)
            {
                InstanceInfo? info = FindByDepth(depth);
                if (info == null || info.Status != InstanceStatus.Suspended) return false;
            }
            return true;
        }
    }

    public InstanceInfo? Find(string key) => _instances.TryGetValue(key, out var info) ? info : null;

    public InstanceInfo? FindByDepth(int depth) => _instances.Values.FirstOrDefault(i => i.Depth == depth);

    public void OnAgentHello(ControlHub.ClientConnection conn, AgentHelloPayload payload)
    {
        _seenMachines.Add(payload.MachineId);
        _agents.Register(payload.MachineId, payload.Capacity, payload.Address);

        if (payload.Instances.Count > 0)
        {
            Log.Info($"에이전트 {payload.MachineId} HELLO — 기존 인스턴스 {payload.Instances.Count}개 보고.");
        }

        if (!_cleanedMachines.Contains(payload.MachineId))
        {
            StartAgentCleanup(payload.MachineId);
            return;
        }

        PrewarmLayers();
    }

    public void TickAgentCleanup()
    {
        DateTime now = DateTime.UtcNow;
        foreach (string machineId in _seenMachines.ToList())
        {
            if (_cleanedMachines.Contains(machineId) || _cleanupPending.Contains(machineId)) continue;
            if (_cleanupRetryAt.TryGetValue(machineId, out DateTime retryAt) && now < retryAt) continue;
            StartAgentCleanup(machineId);
        }
    }

    private void StartAgentCleanup(string machineId)
    {
        if (_cleanedMachines.Contains(machineId) || !_cleanupPending.Add(machineId)) return;

        ControlHub.ClientConnection? conn = _hub.AgentConnection(machineId);
        if (conn == null)
        {
            _cleanupPending.Remove(machineId);
            _cleanupRetryAt[machineId] = DateTime.UtcNow.AddSeconds(2);
            return;
        }

        Log.Info($"에이전트 {machineId} 프로세스 정리 요청.");
        _hub.Send(conn, MessageType.CLEANUP_ALL, null, (ok, reason) =>
        {
            _cleanupPending.Remove(machineId);
            if (!ok)
            {
                _cleanupRetryAt[machineId] = DateTime.UtcNow.AddSeconds(2);
                Log.Info($"에이전트 {machineId} 프로세스 정리 실패: {reason}");
                return;
            }

            _cleanupRetryAt.Remove(machineId);
            _cleanedMachines.Add(machineId);
            Log.Info($"에이전트 {machineId} 프로세스 정리 완료 — 프리웜 시작.");
            PrewarmLayers();
        });
    }

    public void UnregisterAgent(string machineId)
    {
        _agents.Unregister(machineId);
        _cleanedMachines.Remove(machineId);
        _cleanupRetryAt.Remove(machineId);
        foreach (InstanceInfo info in _instances.Values.Where(i => i.MachineId == machineId).ToList())
        {
            _usedPorts.Remove(info.Port);
            info.Status = InstanceStatus.Crashed;
            info.ModConnection = null;
            info.WorldReady = false;
            _runNeeded.Remove(info.Key);
            RequeuePrewarm(info.Depth);
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
        if (_swapDepths.Contains(depth)
            && (info == null || info.Status is InstanceStatus.Stopping
                or InstanceStatus.Stopped or InstanceStatus.Crashed))
        {
            Log.Info($"depth-{depth} 레이어 교체 중 — 접속 배정을 잠시 보류합니다.");
            return null;
        }
        if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info = Spawn(depth);
            if (info == null) return null;
        }

        if (info.Status is InstanceStatus.Suspended or InstanceStatus.Suspending)
        {
            ResumeInstance(info.Key);
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
            Log.Info($"정리 완료된 에이전트가 없거나 수용량 초과 — depth-{depth} 스폰 불가.");
            return null;
        }

        InstanceInfo? info = SpawnOnMachine(depth, machineId, false, "reset", out string? reason);
        if (info == null)
        {
            Log.Info($"depth-{depth} 스폰 불가 — {reason}");
        }
        return info;
    }

    private InstanceInfo? SpawnOnMachine(int depth, string machineId, bool suspendAfterReady,
        string suspendReason, out string? reason)
    {
        reason = null;
        if (!_cleanedMachines.Contains(machineId))
        {
            reason = $"에이전트 {machineId} 정리 완료 전";
            return null;
        }
        if (!_agents.TryReserve(machineId))
        {
            reason = $"에이전트 {machineId} 수용량 초과";
            return null;
        }

        int? allocatedPort = AllocatePort();
        if (allocatedPort == null)
        {
            _agents.DecrementUsage(machineId);
            reason = "인스턴스 포트 고갈";
            return null;
        }
        int port = allocatedPort.Value;
        string key = DepthKey(depth);
        string generation = Guid.NewGuid().ToString("N");
        var info = new InstanceInfo
        {
            Key = key,
            Depth = depth,
            Port = port,
            Generation = generation,
            MachineId = machineId,
            Status = InstanceStatus.Starting,
            SuspendAfterReset = suspendAfterReady,
            SuspendReasonAfterReset = suspendReason,
        };
        _instances[key] = info;
        _runNeeded.Add(key);

        int spawnCleanedUp = 0;
        void CleanupFailedSpawn(string failReason)
        {
            if (Interlocked.Exchange(ref spawnCleanedUp, 1) != 0) return;
            Log.Info($"depth-{depth} SPAWN 실패: {failReason}");
            _agents.DecrementUsage(machineId);
            _usedPorts.Remove(port);

            bool stillCurrent = ReferenceEquals(Find(key), info);
            if (stillCurrent)
            {
                info.Status = InstanceStatus.Crashed;
                ((ICollection<KeyValuePair<string, InstanceInfo>>)_instances)
                    .Remove(new KeyValuePair<string, InstanceInfo>(key, info));
                _runNeeded.Remove(key);
            }
        }

        ControlHub.ClientConnection? conn = _hub.AgentConnection(machineId);
        bool sent = _hub.Send(conn, MessageType.SPAWN, new
        {
            instanceKey = key,
            depth,
            port,
            generation,
            serverName = _config.ServerName,
            serverPassword = _config.ServerPassword,
        }, (ok, failReason) =>
        {
            if (!ok) CleanupFailedSpawn(failReason ?? "unknown");
        });

        if (!sent)
        {
            CleanupFailedSpawn("send failed");
            reason = "SPAWN 전송 실패";
            return null;
        }
        return info;
    }

    private int? AllocatePort()
    {
        for (int p = _config.InstancePortStart; p < _config.InstancePortStart + _config.InstancePortRange; p++)
        {
            if (_usedPorts.Contains(p)) continue;
            _usedPorts.Add(p);
            return p;
        }
        return null;
    }

    private bool IsCleanedMachine(string machineId) => _cleanedMachines.Contains(machineId);

    private string? SelectMachine()
    {
        return _agents.Select(IsCleanedMachine);
    }

    public bool StopInstance(string key) => StopInstance(key, out _);

    public bool StopInstance(string key, out string? reason)
    {
        reason = null;
        InstanceInfo? info = Find(key);
        if (info == null)
        {
            reason = "인스턴스 없음";
            return false;
        }
        if (info.Status == InstanceStatus.Stopping) return true;

        ControlHub.ClientConnection? conn = _hub.AgentConnection(info.MachineId ?? "");
        if (conn == null)
        {
            reason = "에이전트 연결 없음";
            return false;
        }

        InstanceStatus previous = info.Status;
        info.Status = InstanceStatus.Stopping;
        bool sent = _hub.Send(conn, MessageType.STOP,
            new { instanceKey = key, generation = info.Generation });
        if (!sent)
        {
            info.Status = previous;
            reason = "STOP 전송 실패";
            return false;
        }
        return true;
    }

    public bool IsSwapping(int depth) => _swapDepths.Contains(depth);

    public string? ValidateSwap(int depthA, int depthB)
    {
        if (depthA == depthB)
            return "서로 다른 두 레이어를 지정해야 합니다.";
        if (depthA < 1 || depthA > LayerCount || depthB < 1 || depthB > LayerCount)
            return $"레이어 번호는 1~{LayerCount} 범위여야 합니다.";
        if (_swap != null || _swapDepths.Count > 0)
            return "이미 레이어 교체가 진행 중입니다.";

        InstanceInfo? a = FindByDepth(depthA);
        InstanceInfo? b = FindByDepth(depthB);
        if (a == null) return $"L{depthA} 인스턴스가 없습니다.";
        if (b == null) return $"L{depthB} 인스턴스가 없습니다.";
        if (a.Status is not (InstanceStatus.Ready or InstanceStatus.Idle or InstanceStatus.Suspended))
            return $"L{depthA} 교체 불가: 현재 상태 {a.Status}";
        if (b.Status is not (InstanceStatus.Ready or InstanceStatus.Idle or InstanceStatus.Suspended))
            return $"L{depthB} 교체 불가: 현재 상태 {b.Status}";
        if (_prewarmInFlight == depthA || _prewarmInFlight == depthB
            || _prewarmQueued.Contains(depthA) || _prewarmQueued.Contains(depthB))
            return "프리웜 진행 중인 레이어는 교체할 수 없습니다.";
        if (string.IsNullOrEmpty(a.MachineId) || string.IsNullOrEmpty(b.MachineId))
            return "레이어의 에이전트 정보가 없습니다.";
        if (string.Equals(a.MachineId, b.MachineId, StringComparison.Ordinal))
            return $"L{depthA}와 L{depthB}가 같은 에이전트({a.MachineId})에 있습니다.";
        if (!_cleanedMachines.Contains(a.MachineId))
            return $"에이전트 {a.MachineId}가 아직 정리되지 않았습니다.";
        if (!_cleanedMachines.Contains(b.MachineId))
            return $"에이전트 {b.MachineId}가 아직 정리되지 않았습니다.";
        if (_hub.AgentConnection(a.MachineId) == null)
            return $"에이전트 {a.MachineId} 연결이 없습니다.";
        if (_hub.AgentConnection(b.MachineId) == null)
            return $"에이전트 {b.MachineId} 연결이 없습니다.";
        return null;
    }

    public bool TryStartSwap(int depthA, int depthB, out string? reason)
    {
        reason = ValidateSwap(depthA, depthB);
        if (reason != null) return false;

        InstanceInfo a = FindByDepth(depthA)!;
        InstanceInfo b = FindByDepth(depthB)!;
        var swap = new SwapRequest
        {
            DepthA = depthA,
            DepthB = depthB,
            MachineA = a.MachineId!,
            MachineB = b.MachineId!,
            SuspendA = a.Status == InstanceStatus.Suspended,
            SuspendB = b.Status == InstanceStatus.Suspended,
            Deadline = DateTime.UtcNow.AddSeconds(10),
        };
        _swap = swap;
        _swapDepths.Add(depthA);
        _swapDepths.Add(depthB);

        bool stopA = StopInstance(a.Key, out string? stopReasonA);
        bool stopB = StopInstance(b.Key, out string? stopReasonB);
        if (!stopA || !stopB)
        {
            if (stopA) RequeuePrewarm(depthA);
            if (stopB) RequeuePrewarm(depthB);
            _swap = null;
            _swapDepths.Clear();
            reason = stopReasonA ?? stopReasonB ?? "레이어 중지 실패";
            return false;
        }

        Log.Info($"L{depthA}({swap.MachineA}) ↔ L{depthB}({swap.MachineB}) 교체 시작 — " +
            "두 레이어를 중지하고 반대 에이전트에서 재스폰합니다.");
        return true;
    }

    public void TickSwap()
    {
        SwapRequest? swap = _swap;
        if (swap == null) return;

        DateTime now = DateTime.UtcNow;
        switch (swap.Phase)
        {
            case SwapPhase.Stopping:
                if (IsTerminal(swap.DepthA) && IsTerminal(swap.DepthB))
                {
                    InstanceInfo? newA = SpawnOnMachine(swap.DepthA, swap.MachineB,
                        swap.SuspendA, "swap", out string? failA);
                    InstanceInfo? newB = SpawnOnMachine(swap.DepthB, swap.MachineA,
                        swap.SuspendB, "swap", out string? failB);
                    if (newA == null || newB == null)
                    {
                        BeginRollback(swap, failA ?? failB ?? "재스폰 실패");
                        return;
                    }

                    swap.Phase = SwapPhase.WaitingReady;
                    swap.Deadline = now.AddSeconds(_config.InstanceReadyTimeoutSeconds + 30);
                }
                else if (now >= swap.Deadline)
                {
                    Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 교체 중지 타임아웃 — 교체 취소.");
                    _swap = null;
                    _swapDepths.Clear();
                    RequeuePrewarm(swap.DepthA);
                    RequeuePrewarm(swap.DepthB);
                }
                break;

            case SwapPhase.WaitingReady:
                if (IsSettled(swap.DepthA) && IsSettled(swap.DepthB))
                {
                    Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 교체 완료 — " +
                        $"L{swap.DepthA}: {FindByDepth(swap.DepthA)?.MachineId}, " +
                        $"L{swap.DepthB}: {FindByDepth(swap.DepthB)?.MachineId}");
                    _swap = null;
                    _swapDepths.Clear();
                }
                else if (IsTerminal(swap.DepthA) || IsTerminal(swap.DepthB))
                {
                    BeginRollback(swap, "재스폰/월드젠 실패");
                }
                else if (now >= swap.Deadline)
                {
                    BeginRollback(swap, "재스폰 준비 타임아웃");
                }
                break;

            case SwapPhase.RollbackStopping:
                if (IsTerminal(swap.DepthA) && IsTerminal(swap.DepthB))
                {
                    SpawnOriginals(swap);
                }
                else if (now >= swap.Deadline)
                {
                    Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 롤백 중지 타임아웃 — 프리웜 복구에 맡깁니다.");
                    _swap = null;
                    _swapDepths.Clear();
                    RequeuePrewarm(swap.DepthA);
                    RequeuePrewarm(swap.DepthB);
                }
                break;
        }
    }

    private void BeginRollback(SwapRequest swap, string reason)
    {
        Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 교체 실패({reason}) — 원래 배치로 롤백합니다.");

        bool stopping = false;
        foreach (int depth in new[] { swap.DepthA, swap.DepthB })
        {
            InstanceInfo? info = FindByDepth(depth);
            if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed
                or InstanceStatus.Stopping)
                continue;
            stopping |= StopInstance(info.Key);
        }

        if (!stopping)
        {
            SpawnOriginals(swap);
            return;
        }

        swap.Phase = SwapPhase.RollbackStopping;
        swap.Deadline = DateTime.UtcNow.AddSeconds(10);
    }

    private void SpawnOriginals(SwapRequest swap)
    {
        InstanceInfo? restoredA = SpawnOnMachine(swap.DepthA, swap.MachineA,
            swap.SuspendA, "swap", out string? failA);
        InstanceInfo? restoredB = SpawnOnMachine(swap.DepthB, swap.MachineB,
            swap.SuspendB, "swap", out string? failB);
        if (restoredA != null && restoredB != null)
        {
            Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 롤백 완료 — 원래 에이전트로 복구했습니다.");
        }
        else
        {
            Log.Info($"L{swap.DepthA} ↔ L{swap.DepthB} 롤백 일부 실패 — " +
                $"{failA ?? failB ?? "알 수 없음"}. 프리웜 복구를 기다립니다.");
        }

        _swap = null;
        _swapDepths.Clear();
    }

    private bool IsTerminal(int depth)
    {
        InstanceInfo? info = FindByDepth(depth);
        return info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed;
    }

    private static bool IsSettledInfo(InstanceInfo? info) =>
        info?.Status is InstanceStatus.Ready or InstanceStatus.Idle
            or InstanceStatus.Suspended or InstanceStatus.Suspending;

    private bool IsSettled(int depth) => IsSettledInfo(FindByDepth(depth));

    public bool ResetInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (info.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
        {
            Log.Info($"{key} RESET 불가 — 현재 상태 {info.Status}.");
            return false;
        }
        if (info.ModConnection == null)
        {
            Log.Info($"{key} RESET 불가 — 모드 미연결. 크래시 처리.");
            info.Status = InstanceStatus.Crashed;
            return false;
        }

        info.Status = InstanceStatus.IdleReset;
        info.WorldReady = false;
        info.HasPlayedSinceReset = false;
        info.SuspendAfterReset = true;
        info.SuspendReasonAfterReset = "reset";

        _hub.Send(info.ModConnection, MessageType.RESET, new { instanceKey = key }, (ok, reason) =>
        {
            if (!ok)
            {
                Log.Info($"{key} RESET 실패: {reason} — 크래시 처리.");
                info.Status = InstanceStatus.Crashed;
            }
        });
        return true;
    }

    public bool SuspendInstance(string key, string suspendReason = "idle")
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (info.Status == InstanceStatus.Suspended) return true;
        if (info.Status is not (InstanceStatus.Ready or InstanceStatus.Idle)) return false;

        info.Status = InstanceStatus.Suspending;
        bool sent = _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), MessageType.PROCESS_SUSPEND,
            new { instanceKey = key, generation = info.Generation }, (ok, reason) =>
            {
                if (!ok)
                {
                    Log.Info($"{key} SUSPEND 실패: {reason}");
                    if (info.Status == InstanceStatus.Suspending)
                        info.Status = InstanceStatus.Ready;
                    return;
                }

                if (info.Status != InstanceStatus.Suspending) return;
                info.Status = InstanceStatus.Suspended;
                info.LastNonIdleAt = DateTime.UtcNow;
                InstanceSuspended?.Invoke(key, suspendReason);
            });

        if (!sent && info.Status == InstanceStatus.Suspending)
            info.Status = InstanceStatus.Ready;
        return sent;
    }

    public bool ResumeInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (info.Status is InstanceStatus.Ready or InstanceStatus.Idle
            or InstanceStatus.Starting or InstanceStatus.Booting or InstanceStatus.IdleReset)
            return true;
        if (info.Status == InstanceStatus.Resuming) return true;
        if (info.Status is not (InstanceStatus.Suspended or InstanceStatus.Suspending)) return false;

        info.Status = InstanceStatus.Resuming;
        bool sent = _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), MessageType.PROCESS_RESUME,
            new { instanceKey = key, generation = info.Generation }, (ok, reason) =>
            {
                if (!ok)
                {
                    Log.Info($"{key} RESUME 실패: {reason}");
                    if (info.Status == InstanceStatus.Resuming)
                        info.Status = InstanceStatus.Suspended;
                    return;
                }

                if (info.Status != InstanceStatus.Resuming) return;
                if (info.ModConnection != null && info.WorldReady)
                {
                    info.Status = InstanceStatus.Ready;
                    info.LastNonIdleAt = DateTime.UtcNow;
                    InstanceResumed?.Invoke(key);
                }
            });

        if (!sent && info.Status == InstanceStatus.Resuming)
            info.Status = InstanceStatus.Suspended;
        return sent;
    }

    public void PrewarmLayers()
    {
        if (_agents.IsEmpty) return;
        _prewarmRetryAt = DateTime.MinValue;

        for (int depth = 1; depth <= LayerCount; depth++)
        {
            if (_swapDepths.Contains(depth)) continue;
            if (_prewarmInFlight == depth) continue;
            InstanceInfo? existing = FindByDepth(depth);
            if (existing?.Status == InstanceStatus.Suspended) continue;
            if (existing != null && existing.Status is InstanceStatus.Starting or InstanceStatus.Booting
                or InstanceStatus.IdleReset or InstanceStatus.Suspending or InstanceStatus.Resuming)
            {
                if (_prewarmInFlight < 0) BeginPrewarmWait(depth);
                continue;
            }
            if (_prewarmQueued.Contains(depth)) continue;

            _prewarmQueued.Add(depth);
            _prewarmQueue.Enqueue(depth);
        }
    }

    private void RequeuePrewarm(int depth)
    {
        if (_prewarmQueued.Add(depth))
            _prewarmQueue.Enqueue(depth);
    }

    private void BeginPrewarmWait(int depth)
    {
        _prewarmInFlight = depth;
        _prewarmWaitStartedUtc = DateTime.UtcNow;
    }

    public void TickPrewarm()
    {
        DateTime now = DateTime.UtcNow;
        try
        {
            TickPrewarmCore(now);
        }
        finally
        {
            RefreshPrewarmState(now);
        }
    }

    private void TickPrewarmCore(DateTime now)
    {
        if (_cleanedMachines.Count > 0)
        {
            for (int depth = 1; depth <= LayerCount; depth++)
            {
                if (_swapDepths.Contains(depth)) continue;
                InstanceInfo? info = FindByDepth(depth);
                if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
                    RequeuePrewarm(depth);
            }
        }

        if (_prewarmInFlight >= 0)
        {
            InstanceInfo? info = FindByDepth(_prewarmInFlight);
            if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
            {
                int depth = _prewarmInFlight;
                _prewarmInFlight = -1;
                RequeuePrewarm(depth);
            }
            else if (info.Status == InstanceStatus.Suspended)
            {
                _prewarmInFlight = -1;
            }
            else if (info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            {
                SuspendInstance(info.Key, "prewarm");
                return;
            }
            else
            {
                if (now - _prewarmWaitStartedUtc > TimeSpan.FromSeconds(_config.InstanceReadyTimeoutSeconds))
                {
                    Log.Info($"{info.Key} 프리웜 대기 타임아웃 ({info.Status}) — 프로세스 중지 후 재시도.");
                    StopInstance(info.Key);
                    _prewarmInFlight = -1;
                }
                return;
            }
        }

        if (now < _prewarmRetryAt) return;

        while (_prewarmQueue.Count > 0)
        {
            int depth = _prewarmQueue.Dequeue();
            _prewarmQueued.Remove(depth);
            if (_swapDepths.Contains(depth)) continue;
            InstanceInfo? existing = FindByDepth(depth);
            if (existing?.Status == InstanceStatus.Suspended) continue;

            if (existing != null && existing.Status is InstanceStatus.Starting or InstanceStatus.Booting
                or InstanceStatus.IdleReset or InstanceStatus.Suspending or InstanceStatus.Resuming)
            {
                BeginPrewarmWait(depth);
                return;
            }

            if (existing != null && existing.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            {
                BeginPrewarmWait(depth);
                SuspendInstance(existing.Key, "prewarm");
                return;
            }

            if (_agents.AvailableCapacity(IsCleanedMachine) <= 0)
            {
                RequeuePrewarm(depth);
                return;
            }

            string? addr = EnsureInstance(depth);
            if (addr != null)
            {
                BeginPrewarmWait(depth);
                return;
            }

            RequeuePrewarm(depth);
            _prewarmRetryAt = now.AddSeconds(2);
            return;
        }
    }

    private void RefreshPrewarmState(DateTime now)
    {
        if (PrewarmComplete) _prewarmCompletedOnce = true;

        bool ready = _prewarmCompletedOnce
            && CleanedAgentCapacity >= LayerCount
            && AllLayersUsable();
        string notice = ready ? "" : DescribePrewarmWait();

        if (notice != _prewarmNotice)
        {
            _prewarmNotice = notice;
            _prewarmNoticeNextAt = now + PrewarmNoticeInterval;
            Log.Info(notice.Length == 0
                ? $"모든 {LayerCount}개 레이어 프리웜 완료 — 접속 게이트 개방."
                : notice);
        }
        else if (notice.Length > 0 && now >= _prewarmNoticeNextAt)
        {
            Log.Info(notice);
            _prewarmNoticeNextAt = now + PrewarmNoticeInterval;
        }

        if (ready) _admissionEverOpened = true;
        _admissionReady = ready;
    }

    private string DescribePrewarmWait()
    {
        if (_agents.IsEmpty)
            return "프리웜 대기: 연결된 에이전트 없음 — 접속 게이트 닫힘.";

        if (_cleanedMachines.Count == 0)
            return "프리웜 대기: 에이전트 프로세스 정리 완료 대기 중 — 접속 게이트 닫힘.";

        int capacity = CleanedAgentCapacity;
        if (capacity < LayerCount)
        {
            string waiting = string.Join(", ", Enumerable.Range(1, LayerCount)
                .Where(depth => FindByDepth(depth)?.Status != InstanceStatus.Suspended)
                .Select(depth => $"depth-{depth}"));
            string target = waiting.Length == 0 ? "레이어 유지" : $"{waiting} 준비";
            return $"프리웜 대기: capacity {capacity}/{LayerCount} — {target}를 위해 추가 에이전트 필요. 접속 게이트 닫힘.";
        }

        if (!AllLayersUsable())
        {
            int usable = Enumerable.Range(1, LayerCount)
                .Count(depth => IsLayerUsable(FindByDepth(depth)));
            return $"프리웜 진행: 사용 가능 레이어 {usable}/{LayerCount} — 레이어 복구 중. 접속 게이트 닫힘.";
        }

        int prepared = Enumerable.Range(1, LayerCount)
            .Count(depth => FindByDepth(depth)?.Status == InstanceStatus.Suspended);
        return $"프리웜 진행: {prepared}/{LayerCount} 레이어 준비됨. 접속 게이트 닫힘.";
    }

    private bool AllLayersUsable()
    {
        for (int depth = 1; depth <= LayerCount; depth++)
        {
            if (!IsLayerUsable(FindByDepth(depth))) return false;
        }
        return true;
    }

    private static bool IsLayerUsable(InstanceInfo? info) =>
        info != null && info.Status is not (InstanceStatus.Stopped or InstanceStatus.Starting
            or InstanceStatus.Booting or InstanceStatus.Stopping or InstanceStatus.Crashed);

    private readonly HashSet<string> _runNeeded = new();

    public void MarkRunNeeded(string? key)
    {
        if (key == null) return;
        InstanceInfo? info = Find(key);
        if (info != null && info.WorldReady) return;
        _runNeeded.Add(key);
    }

    public void TrySendStartRun(string key)
    {
        if (!_runNeeded.Remove(key)) return;
        InstanceInfo? info = Find(key);
        if (info == null || info.WorldReady) return;
        if (info.Status is not (InstanceStatus.Starting or InstanceStatus.Booting)) return;

        _hub.Send(_hub.ModConnection(key), MessageType.START_RUN, new { instanceKey = key });
    }

    public void OnModHello(ControlHub.ClientConnection conn, ModHelloPayload payload)
    {
        string key = DepthKey(payload.Depth);
        InstanceInfo? info = Find(key);
        if (info == null)
        {
            info = new InstanceInfo
            {
                Key = key,
                Depth = payload.Depth,
                Port = payload.Port,
                Status = InstanceStatus.Booting,
            };
            _instances[key] = info;
            _usedPorts.Add(payload.Port);
        }

        info.Port = payload.Port;
        info.ModConnection = conn;

        if (info.Status == InstanceStatus.Resuming)
        {
            if (info.WorldReady)
            {
                info.Status = InstanceStatus.Ready;
                info.LastNonIdleAt = DateTime.UtcNow;
                InstanceResumed?.Invoke(key);
            }
            return;
        }

        if (info.Status is InstanceStatus.Suspended or InstanceStatus.Suspending)
        {
            return;
        }

        if (info.Status is InstanceStatus.Starting or InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info.Status = InstanceStatus.Booting;
        }

        if (!info.WorldReady)
        {
            _runNeeded.Add(key);
            TrySendStartRun(key);
        }
    }

    public bool OnInstanceReady(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (info.Status != InstanceStatus.Booting && info.Status != InstanceStatus.Starting
            && info.Status != InstanceStatus.IdleReset)
            return false;

        info.WorldReady = true;
        info.Status = InstanceStatus.Ready;
        info.LastNonIdleAt = DateTime.UtcNow;
        return true;
    }

    public bool ShouldSuspendAfterReset(string key) => Find(key)?.SuspendAfterReset == true;

    public void ClearSuspendAfterReset(string key)
    {
        InstanceInfo? info = Find(key);
        if (info != null) info.SuspendAfterReset = false;
    }

    public bool IsQueryable(string key) =>
        Find(key)?.Status is InstanceStatus.Ready or InstanceStatus.Idle;

    public bool IsWebVisible(string key) => IsQueryable(key);

    public bool CanSendModCommand(string? instanceKey, MessageType messageType)
    {
        if (string.IsNullOrEmpty(instanceKey)) return false;
        InstanceInfo? info = Find(instanceKey);
        if (info == null) return false;

        if (messageType == MessageType.MOD_HELLO_ACK) return true;
        if (messageType is MessageType.MAP_WORLD_ACK or MessageType.MAP_BLOCKS_ACK) return true;
        if (messageType == MessageType.START_RUN)
            return (info.Status is InstanceStatus.Starting or InstanceStatus.Booting) && !info.WorldReady;
        if (messageType == MessageType.RESET)
            return info.Status is InstanceStatus.Ready or InstanceStatus.Idle or InstanceStatus.IdleReset;
        if (messageType == MessageType.RUN_RULES_STATE)
            return info.Status is InstanceStatus.Booting or InstanceStatus.Ready
                or InstanceStatus.Idle or InstanceStatus.IdleReset;

        if (messageType is MessageType.PLAYER_DETAIL_REQUEST or MessageType.OBJECT_QUERY
            or MessageType.OBJECT_DETAIL_REQUEST or MessageType.CHAT
            or MessageType.CONSOLE or MessageType.ANNOUNCE)
            return info.Status is InstanceStatus.Ready or InstanceStatus.Idle;

        if (info.Status is InstanceStatus.Suspended or InstanceStatus.Suspending
            or InstanceStatus.Resuming or InstanceStatus.Stopping
            or InstanceStatus.Crashed or InstanceStatus.Stopped)
            return false;

        if (info.Status is InstanceStatus.Starting or InstanceStatus.Booting or InstanceStatus.IdleReset)
            return messageType is MessageType.START_RUN or MessageType.RUN_RULES_STATE or MessageType.MOD_HELLO_ACK;

        return info.Status is InstanceStatus.Ready or InstanceStatus.Idle;
    }

    public void OnInstanceFault(string key, string reason)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        Log.Info($"{key} FAULT: {reason}");
        info.Status = InstanceStatus.Crashed;
        info.ModConnection = null;
        info.WorldReady = false;
        info.SuspendAfterReset = false;
        _runNeeded.Remove(key);
    }

    public void OnModConnectionClosed(ControlHub.ClientConnection conn)
    {
        InstanceInfo? info = _instances.Values.FirstOrDefault(i => i.ModConnection == conn);
        if (info == null) return;
        info.ModConnection = null;
        if (info.Status is InstanceStatus.Suspended or InstanceStatus.Suspending or InstanceStatus.Resuming)
            return;
        if (info.Status is InstanceStatus.Booting or InstanceStatus.Ready or InstanceStatus.Idle
            or InstanceStatus.IdleReset)
        {
            Log.Info($"{info.Key} 모드 연결 종료 — 크래시 처리.");
            info.Status = InstanceStatus.Crashed;
            info.WorldReady = false;
            info.SuspendAfterReset = false;
            _runNeeded.Remove(info.Key);
        }
    }

    public void OnAgentConnectionClosed(ControlHub.ClientConnection conn)
    {
        if (conn.MachineId != null)
        {
            UnregisterAgent(conn.MachineId);
        }
    }

    public bool OnInstanceExited(string key, string generation, int code)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        if (!InstanceGeneration.Matches(info.Generation, generation))
        {
            Log.Info($"{key} stale INSTANCE_EXITED 무시 (gen {generation}, current {info.Generation}, code {code}).");
            return false;
        }

        _usedPorts.Remove(info.Port);
        if (info.MachineId != null)
        {
            _agents.DecrementUsage(info.MachineId);
        }
        info.Status = InstanceStatus.Stopped;
        info.ModConnection = null;
        info.WorldReady = false;
        info.SuspendAfterReset = false;
        _runNeeded.Remove(key);
        return true;
    }

    public void TickIdle(DateTime now, Func<string, int> connectedCountForInstance, int serverOnlineCount)
    {
        if (serverOnlineCount > 0)
        {
            InstanceInfo? l1 = FindByDepth(1);
            if (l1?.Status is InstanceStatus.Suspended or InstanceStatus.Suspending)
                ResumeInstance(l1.Key);
        }

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

            if (now - info.LastNonIdleAt < TimeSpan.FromSeconds(_config.IdleShutdownDelaySeconds))
            {
                if (info.Status == InstanceStatus.Ready)
                    info.Status = InstanceStatus.Idle;
                continue;
            }

            if (info.Depth == 1 && serverOnlineCount > 0)
            {
                info.Status = InstanceStatus.Idle;
                continue;
            }

            if (!info.HasPlayedSinceReset)
            {
                SuspendInstance(info.Key, "idle");
                continue;
            }

            ResetInstance(info.Key);
        }
    }

    public static string DepthKey(int depth) => $"depth-{depth}";
}
