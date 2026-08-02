using System.Text.Json;
using System.Runtime.InteropServices;

namespace CasuMpOrchestrator;

internal static class Program
{
    private static bool _runActive;   // 런 수명주기: DORMANT(false) ↔ ACTIVE(true)

    private static void Main(string[] args)
    {
        // 전역 출력: ConsoleIO(대화형 — 입력줄 보호 + ↑↓ 히스토리) + 타임스탬프 래퍼.
        // rawOut은 SetOut 전에 캡처 — ConsoleIO가 래퍼를 우회해 입력줄/ANSI를 즉시 반영.
        TextWriter rawOut = Console.Out;
        ConsoleIO.Init(rawOut);
        Console.SetOut(new TimestampedConsoleWriter(rawOut));

        string configPath = args.Length > 0 ? args[0] : "orchestrator.json";
        OrchestratorConfig config = OrchestratorConfig.Load(configPath);

        Console.WriteLine($"구성 로드: {configPath}");
        Console.WriteLine($"제어 허브 포트: {config.Port}");

        // 허브 수명 토큰(cts)과 종료 신호(shutdownCts) 분리 — graceful 종료 중에도 허브의
        // 연결이 유지되어야 SHUTDOWN 브로드캐스트가 전달된다. cts를 종료 신호에 직접
        // 사용하면 ControlHub의 per-connection CTS가 링크되어 연결이 먼저 끊겨 전파가 불가능.
        using var cts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        // SIGTERM (kill/데몬) — graceful 종료 캐스케이드. 대화형 Ctrl+C는 ReadKey가 SIGINT를
        // 키로 소비하므로 OperatorConsole.ShutdownRequested(아래)가 담당한다.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

        var hub = new ControlHub(config, cts.Token);
        var runRules = new RunRuleStore(config.RunTablePath, config.RuleTablePath);
        var instances = new InstanceManager(config, hub);
        var sessions = new PlayerSessionStore(config, hub, instances);
        var dataStore = new PlayerDataStore(config);
        var migrations = new MigrationCoordinator(config, hub, instances, sessions, dataStore);

        var banList = new BanList(config.BanListPath);

        hub.OnConnectionClosed += conn =>
        {
            switch (conn.Kind)
            {
                case ClientKind.Gateway:
                    Console.WriteLine("게이트웨이 연결 종료 — 재연결 대기.");
                    break;
                case ClientKind.Agent:
                    instances.OnAgentConnectionClosed(conn);
                    break;
                case ClientKind.Mod:
                    instances.OnModConnectionClosed(conn);
                    break;
            }
        };

        hub.Start();

        var console = new OperatorConsole(sessions, instances, migrations,
            (key, unban) =>
            {
                bool banned = unban != "unban";
                banList.Toggle(key, banned);
                // 게이트웨이에 즉시 전파 — 접속 중이면 킥 (O6-8 전파 구현)
                hub.Send(hub.GatewayConnection, "BAN", new { playerKey = key, banned });
            },
            (key, reason) => hub.Send(hub.GatewayConnection, "KICK", new { playerKey = key, reason }),
            command =>
            {
                // `run <command...>` — 게임 콘솔 명령을 전 온라인 인스턴스에 릴레이 (구 시스템 의미 복원).
                // 모드의 CONSOLE 핸들러(RunModule.HandleConsole)가 실행하고, 실행 결과는
                // 인스턴스 로그(에이전트 DrainAsync)로 자연히 표시된다 — 여기서는 전송만.
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "CONSOLE", new { command });
                }
            });
        // 대화형 Ctrl+C → graceful 종료 (ReadKey가 SIGINT를 소비하므로 키 감지로 연결)
        console.ShutdownRequested = () => shutdownCts.Cancel();
        console.Start();

        // 메인 루프 — 모든 상태 접근은 이 스레드에서만.
        while (!shutdownCts.IsCancellationRequested)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            migrations.Tick();
            // 유휴 정리 계수 (2026-08-02): 마이그레이션 중인 플레이어(Migrating, InstanceId=출발지)와
            // 도착 대기 중인 목적지(tx.TargetInstance)도 인원으로 계산 — FREEZE/READY 대기 중
            // 출발지·목적지가 유휴 오판정으로 강제 정지되는 것을 방지한다.
            instances.TickIdle(DateTime.UtcNow, key =>
                sessions.All.Count(s => s.InstanceId == key && s.Session != PlayerSessionState.Offline)
                + migrations.CountTargeting(key));
            TickRunLifecycle(sessions);
            console.Tick();
            Thread.Sleep(20);
        }

        ConsoleIO.DisableInteractive();
        Console.WriteLine("종료 중 — 데이터 저장 후 스택 종료...");

        // ── 종료 캐스케이드 (오케스트레이터 주도) ──
        // ① SHUTDOWN 브로드캐스트:
        //    모드들 — 접속 플레이어 데이터 제출(동결 제외) + outbound flush + quit
        //    게이트웨이 — 전 세션 Kick + 종료
        //    에이전트 — 전 인스턴스 graceful 정리 + 종료
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }
        hub.SendNoAck(hub.GatewayConnection, "SHUTDOWN", null);
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Agent && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }

        // ② 유예 창: 메인 루프를 계속 돌리며 플레이어 데이터 제출(PLAYER_DATA_SUBMIT)을
        //    수신·디스크 저장한다 (모드가 제출 → 오케스트레이터가 영속화 → 재시작 시 복원).
        DateTime shutdownDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < shutdownDeadline)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            Thread.Sleep(20);
        }

        // ③ 세션 전부 Offline 영속화 — 재시작 시 클린 상태 (스테일 온라인 복원 방지).
        sessions.PersistAllOffline();

        Console.WriteLine("종료 완료.");
        hub.Stop();
        // 허브 백그라운드 태스크(리스너/연결 서비스) 정리 — 프로세스가 곧 종료되지만 명시적 해제.
        cts.Cancel();
    }

    private static void TickRunLifecycle(PlayerSessionStore sessions)    {
        int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
        if (online > 0 && !_runActive)
        {
            _runActive = true;
            Console.WriteLine("런 ACTIVE (첫 플레이어 접속).");
        }
        else if (online == 0 && _runActive)
        {
            _runActive = false;
            Console.WriteLine("런 DORMANT (전원 이탈 — 인스턴스 유휴 정지 대기).");
        }
    }

    private static void Dispatch(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions,
        InstanceManager instances, MigrationCoordinator migrations, PlayerDataStore dataStore,
        BanList banList, RunRuleStore runRules, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "GATEWAY_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                conn.Kind = ClientKind.Gateway;
                conn.GatewayVersion = msg.PayloadAs<HelloVersion>()?.Version;
                Console.WriteLine($"게이트웨이 등록 (version {conn.GatewayVersion}).");
                sessions.PushTableSnapshot();
                // 인증/밴 정보 푸시 — 밴 목록 단일 소유자는 오케스트레이터, 게이트웨이는
                // 메모리 사본으로 접속 시 검증 (O6-8 — 재연결 시 스냅샷 재푸시로 수렴)
                hub.SendNoAck(conn, "AUTH_INFO",
                    new { serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                hub.SendNoAck(conn, "ACK_REPLY", null);
                return;

            case "AGENT_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                var agent = msg.PayloadAs<AgentHelloPayload>();
                if (agent == null) break;
                conn.Kind = ClientKind.Agent;
                conn.MachineId = agent.MachineId;
                conn.AgentCapacity = agent.Capacity;
                conn.AgentAddress = agent.Address;
                instances.RegisterAgent(agent.MachineId, agent.Capacity, agent.Address);
                hub.SendNoAck(conn, "AGENT_HELLO_ACK", new { ok = true });
                return;

            case "MOD_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                var mod = msg.PayloadAs<ModHelloPayload>();
                if (mod == null) break;
                conn.Kind = ClientKind.Mod;
                conn.InstanceKey = mod.InstanceKey;
                conn.InstancePort = mod.Port;
                conn.InstanceDepth = mod.Depth;
                instances.OnModHello(conn, mod);
                hub.SendNoAck(conn, "MOD_HELLO_ACK", new { ok = true });
                hub.SendNoAck(conn, "RUN_RULES_STATE",
                    new { runSettings = runRules.RunSnapshot, rules = runRules.RuleSnapshot });
                return;
        }

        if (conn.Kind == ClientKind.Unknown)
        {
            Console.WriteLine($"HELLO 전 메시지 무시 (conn {conn.Id}): {msg.Type}");
            return;
        }

        // 실시간 로그 릴레이 (agent/gateway → orchestrator) — 타임스탬프와 [source] 접두사는
        // 표시 계층(TimestampedConsoleWriter.PrintRelayed)이 부여한다 (메시지는 클린 상태).
        if (msg.Type == "LOG")
        {
            LogPayload? log = msg.PayloadAs<LogPayload>();
            if (log != null && !string.IsNullOrEmpty(log.Message))
            {
                TimestampedConsoleWriter.Instance?.PrintRelayed(log.Source, log.Message);
            }
            return;
        }

        switch (conn.Kind)
        {
            case ClientKind.Gateway:
                DispatchGateway(config, hub, sessions, migrations, conn, msg);
                break;
            case ClientKind.Agent:
                DispatchAgent(instances, sessions, conn, msg);
                break;
            case ClientKind.Mod:
                DispatchMod(hub, sessions, instances, migrations, dataStore, runRules, conn, msg);
                break;
        }
    }

    private static void DispatchGateway(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions, MigrationCoordinator migrations,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "SESSION_CONNECTED":
                if (TryPlayerKey(msg, out var connected))
                {
                    // 인원 제한 2차 검증 (1차는 게이트웨이 접속 단계 — 여기는 게이트웨이
                    // 카운트가 낡은 경우의 안전망). 초과 시 게이트웨이에 KICK.
                    int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
                    if (online >= config.MaxPlayers)
                    {
                        Console.WriteLine($"인원 초과 — {connected} 접속 거부 (최대 {config.MaxPlayers}).");
                        hub.Send(hub.GatewayConnection, "KICK",
                            new { playerKey = connected.Value, reason = "Server is full." });
                        break;
                    }
                    sessions.OnSessionConnected(connected, GetUsername(msg));
                }
                break;
            case "SESSION_DISCONNECTED":
                if (TryPlayerKey(msg, out var disc))
                {
                    if (migrations.IsMigrating(disc))
                    {
                        // 마이그레이션 중 실퇴장 — 데이터 확정 + Depth 증가 (S7)
                        migrations.OnPlayerQuitDuringMigration(disc);
                    }
                    else
                    {
                        sessions.OnSessionDisconnected(disc);
                    }
                }
                break;
            case "BACKEND_CONNECTED":
                if (TryPlayerKey(msg, out var bc) && msg.PayloadAs<InstancePayload>() is { } bcPayload)
                {
                    sessions.OnBackendConnected(bc, bcPayload.InstanceId);
                    migrations.OnBackendConnected(bc, bcPayload.InstanceId, GetEpoch(msg));
                }
                break;
            case "SWAP_FAILED":
                if (TryPlayerKey(msg, out var sf) && msg.PayloadAs<SwapFailedPayload>() is { } sfPayload)
                    migrations.OnSwapFailed(sf, sfPayload.Reason ?? "unknown");
                break;
            default:
                Console.WriteLine($"게이트웨이 알 수 없는 메시지: {msg.Type}");
                break;
        }
    }

    private static void DispatchAgent(InstanceManager instances, PlayerSessionStore sessions,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "INSTANCE_EXITED":
                if (msg.PayloadAs<InstanceExitedPayload>() is { } exited)
                {
                    instances.OnInstanceExited(exited.InstanceKey, exited.Code);
                    // 정지/크래시 창에 배정 대기 중이던 세션 재배정 (Stopping 엣지 복구)
                    sessions.OnInstanceExited(exited.InstanceKey);
                }
                break;
            default:
                Console.WriteLine($"에이전트 알 수 없는 메시지: {msg.Type}");
                break;
        }
    }

    private static void DispatchMod(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, PlayerDataStore dataStore, RunRuleStore runRules,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "INSTANCE_READY":
                if (msg.PayloadAs<InstanceReadyPayload>() is { } ready
                    && instances.OnInstanceReady(ready.InstanceKey))
                {
                    // ROUTE-ON-READY: 실제 READY 전이 시에만 대기 세션 라우팅 + WaitingReady
                    // 마이그레이션 SWAP 발행 (중복 READY 보고는 무시 — 푸시 1회 보장).
                    sessions.PushRoutesForInstance(ready.InstanceKey);
                    migrations.OnInstanceReady(ready.InstanceKey);
                }
                break;
            case "INSTANCE_FAULT":
                if (msg.PayloadAs<InstanceFaultPayload>() is { } fault)
                    instances.OnInstanceFault(fault.InstanceKey, fault.Reason ?? "unknown");
                break;
            case "LAYER_END":
                if (msg.PayloadAs<LayerEndPayload>() is { } layerEnd && TryPlayerKey(layerEnd.PlayerKey, out var le))
                    migrations.OnLayerEnd(le, layerEnd.FromDepth, layerEnd.MaxLayers);
                break;
            case "RESPAWN":
                if (msg.PayloadAs<RespawnPayload>() is { } respawn && TryPlayerKey(respawn.PlayerKey, out var rk))
                    migrations.OnRespawnRequest(rk, respawn.FromDepth);
                break;
            case "FREEZE_DONE":
                if (TryPlayerKey(msg, out var fd))
                    migrations.OnFreezeDone(fd, GetEpoch(msg));
                break;
            case "RESUME_DONE":
                if (TryPlayerKey(msg, out var rd))
                    migrations.OnResumeDone(rd, GetEpoch(msg));
                break;
            case "WORLDGEN_DONE":
                if (TryPlayerKey(msg, out var wg))
                    migrations.OnWorldgenDone(wg, GetEpoch(msg));
                break;
            case "PLAYER_LEFT":
                if (TryPlayerKey(msg, out var pl))
                    migrations.OnPlayerLeftDuringMigration(pl, GetEpoch(msg));
                break;
            case "PLAYER_DATA_SUBMIT":
                if (TryPlayerKey(msg, out var ps) && msg.Payload is { } submitPayload
                    && submitPayload.TryGetProperty("payload", out JsonElement innerPayload))
                {
                    bool migration = migrations.IsMigrating(ps);
                    dataStore.OnSubmit(ps, innerPayload, migration);
                }
                break;
            case "PLAYER_DATA_REQUEST":
                if (TryPlayerKey(msg, out var pr))
                    dataStore.OnRequest(pr, conn, hub);
                break;
            case "CHAT":
                if (msg.PayloadAs<ChatPayload>() is { } chat && !string.IsNullOrEmpty(chat.Speaker))
                {
                    // 크로스 인스턴스 채팅 릴레이 (Phase 2): 플레이어 메시지만 전파 —
                    // 발신자 제외 전 인스턴스에 레이어 태그("L" + 발신 depth)를 부여해 재전송.
                    string layerLabel = $"L{conn.InstanceDepth}";
                    int forwarded = 0;
                    foreach (ControlHub.ClientConnection other in hub.Connections
                        .Where(c => c.Kind == ClientKind.Mod && !c.Closed && c != conn))
                    {
                        hub.SendNoAck(other, "CHAT", new
                        {
                            speaker = chat.Speaker,
                            message = chat.Message,
                            color = chat.Color,
                            layer = layerLabel,
                        });
                        forwarded++;
                    }
                    if (forwarded > 0)
                    {
                        Console.WriteLine($"{chat.Speaker} → {forwarded}개 인스턴스 릴레이 (L{conn.InstanceDepth}).");
                    }
                }
                break;
            case "LIST_REQUEST":
                // !list — 전 세션 depth별 집계 응답 (라인 배열 — 모드가 라인별 개인 회신).
                if (msg.PayloadAs<ListRequestPayload>() is { } listReq)
                {
                    string[] lines = BuildPlayerListLines(sessions);
                    hub.SendNoAck(conn, "LIST_RESULT", new { playerKey = listReq.PlayerKey, lines });
                    Console.WriteLine($"!list 요청 — {listReq.PlayerKey} ({lines.Length}줄).");
                }
                break;
            case "CURRENT_REQUEST":
                // !currentrun [key] — RunRuleStore 단일 소유자 조회.
                if (msg.PayloadAs<CurrentRequestPayload>() is { } curReq)
                {
                    string text = BuildRunSettingsText(runRules, curReq.Key);
                    hub.SendNoAck(conn, "CURRENT_RESULT", new { playerKey = curReq.PlayerKey, text });
                    Console.WriteLine($"!currentrun 요청 — {curReq.PlayerKey} (key={curReq.Key}).");
                }
                break;
            case "CALLADMIN":
                if (msg.PayloadAs<CallAdminPayload>() is { } callAdmin)
                {
                    Console.WriteLine($"{callAdmin.Username} ({callAdmin.PlayerKey}) — 관리자 호출.");
                }
                break;
            default:
                Console.WriteLine($"모드 알 수 없는 메시지: {msg.Type}");
                break;
        }
    }

    private static bool TryPlayerKey(ControlMessage msg, out PlayerKey key)
    {
        key = default;
        string? value = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    private static bool TryPlayerKey(string? value, out PlayerKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    /// <summary>마이그레이션 이벤트 payload의 epoch 추출 (없으면 null — 하위 호환).</summary>
    private static int? GetEpoch(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("epoch", out JsonElement e) && e.TryGetInt32(out int v) ? v : null;
    }

    /// <summary>SESSION_CONNECTED payload의 username 추출 (없으면 null).</summary>
    private static string? GetUsername(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("username", out JsonElement u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }

    /// <summary>!list 응답 라인 배열 — 온라인 세션을 depth별로 집계, 레이어당 한 줄
    /// ("[L1]: 이름들"). 모드가 각 줄을 별도의 개인 채팅으로 표시한다.</summary>
    private static string[] BuildPlayerListLines(PlayerSessionStore sessions)
    {
        var byDepth = sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .GroupBy(s => s.Depth)
            .OrderBy(g => g.Key)
            .ToList();
        if (byDepth.Count == 0) return new[] { "접속 중인 플레이어가 없습니다." };

        return byDepth.Select(g =>
            $"[L{g.Key}] {string.Join(", ", g.Select(s => s.Username ?? s.Key.Value))}").ToArray();
    }

    /// <summary>!currentrun 응답 텍스트 — 키 조회 또는 전체 목록 (RunRuleStore가 정본).</summary>
    private static string BuildRunSettingsText(RunRuleStore runRules, string key)
    {
        var snapshot = runRules.RunSnapshot;
        if (!string.IsNullOrEmpty(key))
        {
            return snapshot.TryGetValue(key, out string? value)
                ? $"{key}: {value}"
                : $"설정을 찾을 수 없습니다: {key}";
        }
        return string.Join("\n", snapshot.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    // ── payload 모델 ──

    private sealed class HelloVersion { public int Version { get; set; } }
    private sealed class AgentHelloPayload { public string MachineId { get; set; } = ""; public int Capacity { get; set; } public string Address { get; set; } = ""; }
    private sealed class InstanceExitedPayload { public string InstanceKey { get; set; } = ""; public int Code { get; set; } }
    private sealed class InstanceReadyPayload { public string InstanceKey { get; set; } = ""; }
    private sealed class InstanceFaultPayload { public string InstanceKey { get; set; } = ""; public string? Reason { get; set; } }
    private sealed class LayerEndPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } public int MaxLayers { get; set; } }

    /// <summary>!respawn 요청 (mod → orchestrator) — fromDepth는 발신 인스턴스 실제 depth.</summary>
    private sealed class RespawnPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } }

    /// <summary>실시간 로그 릴레이 (agent/gateway → orchestrator — LOG).</summary>
    private sealed class LogPayload { public string Source { get; set; } = ""; public string Message { get; set; } = ""; }
    private sealed class PlayerKeyPayload { public string? PlayerKey { get; set; } }
    private sealed class InstancePayload { public string InstanceId { get; set; } = ""; }
    private sealed class SwapFailedPayload { public string? Reason { get; set; } }

    /// <summary>크로스 인스턴스 채팅 payload (mod → orchestrator → mod).</summary>
    private sealed class ChatPayload
    {
        public string Speaker { get; set; } = "";
        public string Message { get; set; } = "";
        public string Color { get; set; } = "";
    }

    /// <summary>!list 요청 (mod → orchestrator).</summary>
    private sealed class ListRequestPayload
    {
        public string PlayerKey { get; set; } = "";
    }

    /// <summary>!currentrun 요청 (mod → orchestrator).</summary>
    private sealed class CurrentRequestPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Key { get; set; } = "";
    }

    /// <summary>!calladmin 보고 (mod → orchestrator — Discord는 후속).</summary>
    private sealed class CallAdminPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Username { get; set; } = "";
    }
}

/// <summary>밴 목록 — 중앙 원본, 게이트웨이에 BAN 명령 전파 (O6-8).</summary>
public sealed class BanList
{
    private readonly string _path;
    private readonly HashSet<string> _banned = new();

    public BanList(string path)
    {
        _path = path;
        Load();
    }

    public void Toggle(string playerKey, bool banned)
    {
        if (banned) _banned.Add(playerKey);
        else _banned.Remove(playerKey);
        Save();
    }

    /// <summary>전체 목록 (게이트웨이 AUTH_INFO 푸시용).</summary>
    public IReadOnlyCollection<string> All => _banned.ToList();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (string key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [])
                _banned.Add(key);
        }
        catch { }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_banned.ToArray())); }
        catch { }
    }
}
