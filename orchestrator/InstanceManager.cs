using System.Text.Json;
using System.IO;

namespace CasuMpOrchestrator;

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
    // 마지막 레이어 초기화(RESET) 이후 플레이어가 접속했었는지 - Prewarm 인스턴스의
    // 유휴 재초기화를 "점유 사이클당 1회"로 제한 (플레이어 없이 무한 월드젠 루프 방지)
    public bool HasPlayedSinceReset { get; set; }
}

// 인스턴스 수명주기 - 노드 에이전트 경유 스폰/정지, READY 검증
// 모든 접근은 메인 스레드 전용
public sealed class InstanceManager
{
    // 프리웜 고정 레이어 - 에이전트 등록 시 항상 스폰/유지 (유휴 시 RESET 후 대기)
    private const int PrewarmDepth = 1;

    private readonly OrchestratorConfig _config;
    private readonly ControlHub _hub;

    private readonly Dictionary<string, InstanceInfo> _instances = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly Dictionary<string, (int Used, int Capacity, string Address)> _agents = new();

    public InstanceManager(OrchestratorConfig config, ControlHub hub)
    {
        _config = config;
        _hub = hub;
        LoadPreferredPrewarmAgent();
    }

    public IReadOnlyCollection<InstanceInfo> All => _instances.Values.ToList();

    public InstanceInfo? Find(string key) => _instances.TryGetValue(key, out var info) ? info : null;

    public InstanceInfo? FindByDepth(int depth) => _instances.Values.FirstOrDefault(i => i.Depth == depth);

    // 에이전트 등록 (AGENT_HELLO)

    public void RegisterAgent(string machineId, int capacity, string address)
    {
        _agents[machineId] = (0, capacity, address);
        VerboseState.Line($"에이전트 등록: {machineId} (수용 {capacity}, 주소 {address})");
    }

    public void UnregisterAgent(string machineId)
    {
        _agents.Remove(machineId);
        foreach (InstanceInfo info in _instances.Values.Where(i => i.MachineId == machineId))
        {
            info.Status = InstanceStatus.Crashed;
            Console.WriteLine($"에이전트 {machineId} 이탈 — 인스턴스 {info.Key} 크래시 처리");
        }
        VerboseState.Line($"에이전트 등록 해제: {machineId}");
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

    // 스폰 (G-2 파라미터 세트)

    // 해당 depth의 인스턴스를 보장 (없으면 SPAWN). 백엔드 주소 반환 - 부팅 중이어도
    // 게이트웨이가 백엔드 재시도로 커버 . Stopped/Crashed 기록은 죽은 프로세스이므로
    // 재스폰한다 (유휴 정지 후 재접속 시 고아 주소로 라우팅되는 것 방지)
    // preferredMachine: 프리웜 경로의 선호 에이전트 (등록+여유 시 우선, 아니면 알고리즘)
    public string? EnsureInstance(int depth, string? preferredMachine = null)
    {
        InstanceInfo? info = FindByDepth(depth);
        if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            if (info != null)
            {
                VerboseState.Line($"{info.Key} 재스폰 (상태 {info.Status}).");
            }
            info = Spawn(depth, preferredMachine);
            if (info == null) return null;
        }
        return BackendAddrFor(info);
    }

    private InstanceInfo? Spawn(int depth, string? preferredMachine = null)
    {
        if (_agents.Count == 0)
        {
            Console.WriteLine($"에이전트가 없어 depth-{depth} 스폰 불가.");
            return null;
        }

        // 배치: 선호 에이전트(등록+여유) 우선, 아니면 수용량 대비 사용량이 가장 적은 에이전트
        string? machineId = SelectMachine(preferredMachine);
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

        VerboseState.Line($"depth-{depth} 스폰 요청 → {machineId} (포트 {port})");
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

    // 에이전트 선택 - preferred가 등록되고 여유(Used &lt; Capacity)가 있으면 그것을
    // 우선하고, 아니면 수용량 대비 사용량이 가장 낮은 에이전트로 폴백한다
    private string? SelectMachine(string? preferred)
    {
        if (preferred != null
            && _agents.TryGetValue(preferred, out var pref)
            && pref.Used < pref.Capacity)
        {
            return preferred;
        }
        return _agents
            .Where(kv => kv.Value.Used < kv.Value.Capacity)
            .OrderBy(kv => (double)kv.Value.Used / kv.Value.Capacity)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    // 프리웜 전용 선호 에이전트 - `prewarm set <agent>`로 지정 (미연결/포화 시
    // SelectMachine이 알고리즘으로 폴백). 수요 스폰(마이그레이션/접속)에는 적용되지 않는다
    // pwd/prewarm-agent.json에 영속화 - 재시작 후에도 유지된다
    private string? _preferredPrewarmAgent;
    private static readonly string PrewarmAgentPath =
        Path.Combine(Directory.GetCurrentDirectory(), "prewarm-agent.json");

    public void SetPreferredPrewarmAgent(string? machineId)
    {
        _preferredPrewarmAgent = machineId;
        try
        {
            File.WriteAllText(PrewarmAgentPath,
                JsonSerializer.Serialize(new { agent = machineId }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"prewarm-agent.json 저장 실패: {ex.Message}");
        }
    }

    public string? PreferredPrewarmAgent => _preferredPrewarmAgent;

    private void LoadPreferredPrewarmAgent()
    {
        try
        {
            if (File.Exists(PrewarmAgentPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(PrewarmAgentPath));
                if (doc.RootElement.TryGetProperty("agent", out JsonElement el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    _preferredPrewarmAgent = el.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"prewarm-agent.json 로드 실패: {ex.Message}");
        }
    }

    public void StopInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        info.Status = InstanceStatus.Stopping;
        _hub.Send(_hub.AgentConnection(info.MachineId ?? ""), "STOP", new { instanceKey = key });
        VerboseState.Line($"{key} 정지 요청.");
    }

    // Prewarm 인스턴스 레이어 초기화 - 프로세스 생존 상태에서 모드에 RESET을
    // 발신해 ToMainMenu -> 재월드젠 후 READY 재보고를 유도한다 (유휴 대기 유지 - 부팅 비용 제거)
    // 실패(모드 미연결/ack 타임아웃) 시 Crashed 처리 - 다음 EnsureInstance가 재스폰 폴백
    public void ResetInstance(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return;
        if (info.ModConnection == null)
        {
            Console.WriteLine($"{key} RESET 불가 — 모드 미연결. 크래시 처리.");
            info.Status = InstanceStatus.Crashed;
            return;
        }
        info.Status = InstanceStatus.IdleReset;
        _hub.Send(info.ModConnection, "RESET", new { instanceKey = key }, (ok, reason) =>
        {
            if (!ok)
            {
                Console.WriteLine($"{key} RESET 실패: {reason} — 크래시 처리.");
                info.Status = InstanceStatus.Crashed;
            }
        });
        VerboseState.Line($"{key} 레이어 초기화 요청 (RESET).");
    }

    // 프리웜 - 레이어 1을 수용량 내 최대한 스폰 (에이전트 등록마다 재실행 - 누락분 자동
    // 보충, 기존 인스턴스는 재스폰 없음). 동시 부팅 리소스 경쟁으로 월드젠이 실패하는 것을
    // 방지하기 위해 큐에 넣고 TickPrewarm이 순차 처리한다 (실측: 3개 동시 스폰 시 1번만
    // 월드젠 성공)
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
            // 이미 부팅/월드젠 중 - 그 인스턴스가 순차 처리의 현재 항목
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
    // Prewarm 인스턴스 수동 정지 -> 재시작 예약 (OnInstanceExited가 실제 종료 시점에
    // _prewarmRestartAt으로 이전). 키: 인스턴스 키
    private readonly Dictionary<string, TimeSpan> _prewarmRestartRequested = new();
    private readonly Dictionary<string, DateTime> _prewarmRestartAt = new();

    // Prewarm 인스턴스 정지 후 자동 재시작 예약 - 실제 종료(OnInstanceExited) 시점부터
    // delay 후 재스폰된다 (instance stop의 유지 관리 용도)
    public void SchedulePrewarmRestart(string key, TimeSpan delay)
        => _prewarmRestartRequested[key] = delay;

    // depth가 Prewarm 레이어인지 - instance spawn 금지/자동 재시작 대상 판정
    public bool IsPrewarmDepth(int depth) => depth == PrewarmDepth;

    // 순차 프리웜 진행 (메인 루프 틱) - 현재 항목이 READY(월드젠 완료)가 되면
    // 다음을 스폰한다. 실패(스폰 불가/크래시) 시 현재 항목을 버리고 다음으로 넘어간다
    // (누락분은 다음 에이전트 등록의 PrewarmLayers가 재보충)
    public void TickPrewarm()
    {
        // 수동 정지 -> 자동 재시작 예약 처리 (실제 종료 후 유예 경과 시 재스폰)
        foreach (var kv in _prewarmRestartAt.ToList())
        {
            if (DateTime.UtcNow < kv.Value) continue;
            _prewarmRestartAt.Remove(kv.Key);
            InstanceInfo? info = Find(kv.Key);
            if (info != null && info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
            {
                int depth = info.Depth;
                if (!_prewarmQueue.Contains(depth) && depth != _prewarmInFlight)
                {
                    _prewarmQueue.Enqueue(depth);
                }
            }
        }

        if (_prewarmInFlight >= 0)
        {
            InstanceInfo? info = FindByDepth(_prewarmInFlight);
            if (info == null || info.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
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
            string? addr = EnsureInstance(depth, _preferredPrewarmAgent);
            if (addr != null)
            {
                _prewarmInFlight = depth;
                return; // 스폰됨 - READY까지 대기
            }
            // 스폰 불가 (에이전트 없음/수용량 초과) - 이 항목은 스킵, 다음 시도
        }
    }

    // 런 시작 대기 표시 (G-1 개정) - 스폰 시점과 모드 연결(MOD_HELLO) 시점에
    // 표시되고, MOD_HELLO에서 바로 START_RUN을 전송한다. 인스턴스 수명당 1회 전송
    // (프리웜: 월드젠은 플레이어 접속과 무관하게 인스턴스 부팅 직후 시작)
    private readonly HashSet<string> _runNeeded = new();

    public void MarkRunNeeded(string? key)
    {
        if (key == null) return;
        // 이미 실행 중(READY/IDLE)인 인스턴스는 런 시작 불필요 (인스턴스 수명당 1회)
        InstanceInfo? info = Find(key);
        if (info != null && info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
            return;
        _runNeeded.Add(key);
    }

    // 대기 중이던 START_RUN 전송 (인스턴스 수명당 1회)
    public void TrySendStartRun(string key)
    {
        if (!_runNeeded.Remove(key)) return;
        _hub.Send(_hub.ModConnection(key), "START_RUN", new { instanceKey = key });
    }

    // 모드/에이전트 이벤트

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
            VerboseState.Line($"{key} 재수용 (기존 인스턴스).");
        }
        info.Port = payload.Port;
        info.ModConnection = conn;
        if (info.Status is InstanceStatus.Starting or InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            info.Status = InstanceStatus.Booting;
        }
        VerboseState.Line($"{key} 모드 연결 (포트 {payload.Port}).");

        // 프리웜 (G-1 개정): START_RUN은 첫 플레이어 백엔드 연결이 아니라 모드 연결 시점에 발신
        // 월드젠이 플레이어 없이 진행되어, 접속/마이그레이션이 목적지 READY 이후에만 이뤄진다
        // (콜드 인스턴스의 announce 스테일 시드/월드젠 중 접속 거절 문제를 구조적으로 제거)
        // 재수용/재시작에도 안전 - 모드 측 HandleStartRun이 이미 생성된 세계면 무시한다
        _runNeeded.Add(key);
        TrySendStartRun(key);
    }

    // 인스턴스 READY 전이 (Booting/Starting -> Ready). 실제 전이된 경우에만 true
    // 중복 INSTANCE_READY 보고(모드 재보고 등)는 false를 반환해 호출부가
    // ROUTE-ON-READY 푸시를 1회만 수행하도록 한다 ( 중복 푸시 회귀 수정)
    public bool OnInstanceReady(string key)
    {
        InstanceInfo? info = Find(key);
        if (info == null) return false;
        // 정지/크래시 중인 인스턴스는 READY로 부활시키지 않는다 (중복 정지 방지)
        if (info.Status != InstanceStatus.Booting && info.Status != InstanceStatus.Starting
            && info.Status != InstanceStatus.IdleReset)
            return false;
        info.Status = InstanceStatus.Ready;
        // 유휴 유예 기준 갱신 - 부팅/리셋 완료 후 유휴 타이머를 깨끗하게 재시작
        info.LastNonIdleAt = DateTime.UtcNow;
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
        VerboseState.Line($"{key} 종료 (code {code}).");

        // Prewarm 인스턴스 수동 정지 -> 실제 종료 시점부터 유예 후 자동 재시작 예약
        if (_prewarmRestartRequested.Remove(key, out TimeSpan delay))
        {
            _prewarmRestartAt[key] = DateTime.UtcNow + delay;
            VerboseState.Line($"{key} Prewarm 자동 재시작 예약 ({delay.TotalSeconds:F0}초 후).");
        }
    }

    // 유휴 정리 ( 인스턴스 수명주기 + Prewarm 유휴 재초기화)

    public void TickIdle(DateTime now, Func<string, int> connectedCountForInstance)
    {
        foreach (InstanceInfo info in _instances.Values.ToList())
        {
            // Starting/Booting/IdleReset은 유휴 판정 대상 아님 (부팅/리셋 중 - READY 대기)
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
            if (isPrewarm && !info.HasPlayedSinceReset)
            {
                // 이미 초기화된 신선한 월드 - 플레이어 없이 무한 재월드젠 금지
                info.Status = InstanceStatus.Idle;
                continue;
            }

            if (isPrewarm)
            {
                // Prewarm 인스턴스 - 프로세스 생존 + 레이어 초기화 후 유휴 대기
                info.HasPlayedSinceReset = false;
                ResetInstance(info.Key);
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
