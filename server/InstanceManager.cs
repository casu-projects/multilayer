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

    private static readonly TimeSpan PrewarmNoticeInterval = TimeSpan.FromSeconds(60);

    private readonly HashSet<string> _seenMachines = new();
    private readonly HashSet<string> _cleanedMachines = new();
    private readonly HashSet<string> _cleanupPending = new();
    private readonly Dictionary<string, DateTime> _cleanupRetryAt = new();

    public InstanceManager(ServerConfig config, ControlHub hub)
    {
        _config = config;
        _hub = hub;
    }

    public IReadOnlyCollection<InstanceInfo> All => _instances.Values.ToList();

    public event Action<string>? InstanceResumed;

    public bool AdmissionReady => _admissionReady;

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

        int? allocatedPort = AllocatePort();
        if (allocatedPort == null)
        {
            Log.Info($"인스턴스 포트 고갈 — depth-{depth} 스폰 불가.");
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
        };
        _instances[key] = info;
        _runNeeded.Add(key);

        _agents.IncrementUsage(machineId);

        int spawnCleanedUp = 0;
        void CleanupFailedSpawn(string reason)
        {
            if (Interlocked.Exchange(ref spawnCleanedUp, 1) != 0) return;
            Log.Info($"depth-{depth} SPAWN 실패: {reason}");
            info.Status = InstanceStatus.Crashed;
            _agents.DecrementUsage(machineId);
            _usedPorts.Remove(port);
            ((ICollection<KeyValuePair<string, InstanceInfo>>)_instances)
                .Remove(new KeyValuePair<string, InstanceInfo>(key, info));
            _runNeeded.Remove(key);
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
        }, (ok, reason) =>
        {
            if (!ok) CleanupFailedSpawn(reason ?? "unknown");
        });

        if (!sent)
        {
            CleanupFailedSpawn("send failed");
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

    public void StopInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        info.Status = InstanceStatus.Stopping;
        _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), MessageType.STOP,
            new { instanceKey = key, generation = info.Generation });
    }

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

    public bool SuspendInstance(string key)
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
                SuspendInstance(info.Key);
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
                SuspendInstance(existing.Key);
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
                SuspendInstance(info.Key);
                continue;
            }

            ResetInstance(info.Key);
        }
    }

    public static string DepthKey(int depth) => $"depth-{depth}";
}
