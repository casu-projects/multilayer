using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;

namespace CasuMpOrchestrator;

/// <summary>락다운 활성 상태 — 메모리 전용 (영속화 없음 — 오케스트레이터 재시작 시 해제).
/// 게이트웨이 재연결(GATEWAY_HELLO) 시 상태 재푸시용으로 Program이 참조한다.</summary>
public static class LockdownState
{
    public static bool Active;
    public static ulong[] Bypass = Array.Empty<ulong>();
}

/// <summary>디버그 로그 표시 상태 — `verbose on/off` 명령이 설정 + orchestrator.json 영속화.
/// 게이트웨이/에이전트/모드에 VERBOSE 메시지로 전파된다 (오케스트레이터 자체도 게이트).</summary>
public static class VerboseState
{
    public static bool Active;

    /// <summary>디버그급 콘솔 출력 — verbose=false면 숨김.</summary>
    public static void Line(string message)
    {
        if (Active) Console.WriteLine(message);
    }
}

/// <summary>운영자 콘솔 — stdin 명령 (G-7 확장).
/// `exec <command...>` — 매개변수 전체를 게임 콘솔 명령으로 전 인스턴스에 릴레이
/// (구 시스템 의미 복원 — 설정 변경이 아니라 게임 명령 실행).
/// `rule <이름> <값>` / `run <설정> <값>` — rule.json/run.json 수정 + 전 인스턴스 실시간 반영.
/// `lockdown` — 락다운 토글 (현재 상태에 따라 점검 모드 시작/종료).</summary>
public sealed class OperatorConsole
{
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly ControlHub _hub;
    private readonly PlayerSessionStore _sessions;
    private readonly InstanceManager _instances;
    private readonly MigrationCoordinator _migrations;
    private readonly Action<string, string?> _banAction;   // (playerKey, reason)
    private readonly Action<string, string?> _kickAction;
    private readonly Action<string>? _consoleRelay;   // (game command) — 전 인스턴스 게임 콘솔 실행
    private readonly RunRuleStore _runRuleStore;      // rule.json/run.json 정본
    private readonly Action? _pushRulesAction;        // 변경 후 전 인스턴스 RUN_RULES_STATE 푸시
    private readonly OrchestratorConfig _config;
    private readonly BanList _banList;
    private readonly string _configPath;

    /// <summary>종료 신호 수신 시 호출 (Program.Main에서 cts.Cancel과 연결 — 대화형 Ctrl+C용).</summary>
    internal Action? ShutdownRequested;

    /// <summary>락다운 시작/종료 알림 (Program이 DiscordBot.SendLockdownAsync로 연결 — true=시작).</summary>
    internal Action<bool>? LockdownNotify;

    public OperatorConsole(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, Action<string, string?> banAction, Action<string, string?> kickAction,
        RunRuleStore runRuleStore, Action? pushRulesAction = null, Action<string>? consoleRelay = null,
        OrchestratorConfig? config = null, BanList? banList = null, string configPath = "")
    {
        _hub = hub;
        _sessions = sessions;
        _instances = instances;
        _migrations = migrations;
        _banAction = banAction;
        _kickAction = kickAction;
        _runRuleStore = runRuleStore;
        _pushRulesAction = pushRulesAction;
        _consoleRelay = consoleRelay;
        _config = config ?? new OrchestratorConfig();
        _banList = banList ?? new BanList("");
        _configPath = configPath;

        RegisterCommands();
    }

    // ── 명령어 등록 (등록 패턴 — 2026-08-08) ──
    // 핸들러는 argv(전체 분할)를 받아 출력 라인 배열 반환 (null = 출력 없음).
    // deps는 생성자 필드를 클로저로 캡처한다. help는 자동 생성 (레지스트리 내장).
    private void RegisterCommands()
    {
        ConsoleCommands.Register("help", "명령어 목록을 표시합니다", _ => ConsoleCommands.GetHelpLines());

        ConsoleCommands.Register("list", "유저 목록을 표시합니다", argv =>
        {
            var online = _sessions.All
                .Where(p => p.Session != PlayerSessionState.Offline)
                .OrderBy(p => p.Depth)
                .ThenBy(p => p.Username ?? p.Key.Value)
                .ToList();
            if (online.Count == 0) return new[] { "현재 총 0명 접속 중 :" };
            var lines = new List<string> { $"현재 총 {online.Count}명 접속 중 :" };
            lines.AddRange(online.Select(p => $"{p.Username ?? p.Key.Value} (L{p.Depth}, {p.Key.Value})"));
            return lines.ToArray();
        });

        ConsoleCommands.Register("kick", "특정 유저를 추방합니다", argv =>
        {
            string arg = string.Join(" ", argv.Skip(1));
            if (TryResolvePlayer(arg, out PlayerKey kick)) _kickAction(kick.Value, "Kicked by operator.");
            return null;
        });

        ConsoleCommands.Register("lockdown", "점검 모드 토글 (전원 추방 + LockdownBypass만 접속 허용 + 타이틀 접미)", argv =>
        {
            ToggleLockdown();
            return null;
        });

        ConsoleCommands.Register("verbose", "디버그 로그 표시 토글 (orchestrator.json 영속 반영)", argv =>
        {
            ToggleVerbose();
            return null;
        });

        ConsoleCommands.Register("ban", "특정 유저를 차단합니다", argv =>
        {
            string arg = string.Join(" ", argv.Skip(1));
            if (TryResolvePlayer(arg, out PlayerKey ban)) _banAction(ban.Value, null);
            return null;
        });

        ConsoleCommands.Register("unban", "특정 유저의 차단을 해제합니다", argv =>
        {
            string arg = string.Join(" ", argv.Skip(1));
            if (TryResolvePlayer(arg, out PlayerKey unban)) _banAction(unban.Value, "unban");
            return null;
        });

        ConsoleCommands.Register("instance", "인스턴스 상태/조작", HandleInstance);

        ConsoleCommands.Register("clear", "터미널 화면을 지웁니다", argv =>
        {
            ConsoleIO.ClearScreen();
            return null;
        });

        ConsoleCommands.Register("connections", "연결된 게이트웨이/에이전트/인스턴스(모드) 목록", argv => HandleConnections());

        ConsoleCommands.Register("prewarm", "Prewarm 선호 에이전트 지정/해제", HandlePrewarm);

        ConsoleCommands.Register("migrate", "특정 유저를 수동 마이그레이션합니다 (기본: 다음 레이어)", HandleMigrate);

        ConsoleCommands.Register("exec", "모든 인스턴스에 게임 콘솔 명령을 실행합니다", argv =>
        {
            HandleRun(string.Join(" ", argv.Skip(1)));
            return null;
        });

        ConsoleCommands.Register("rule", "rule.json 수정 + 전 인스턴스 즉시 반영 (1/0 → True/False)", argv =>
        {
            HandleSet("rule", string.Join(" ", argv.Skip(1)), isRun: false);
            return null;
        });

        ConsoleCommands.Register("run", "run.json 수정 + 전 인스턴스 즉시 반영 (1/0 → True/False)", argv =>
        {
            HandleSet("run", string.Join(" ", argv.Skip(1)), isRun: true);
            return null;
        });
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            if (ConsoleIO.Interactive)
            {
                ReadStdinLoopInteractive();
            }
            else
            {
                ReadStdinLoopBlocking();
            }
        })
        { IsBackground = true, Name = "Orchestrator.StdinReader" };
        thread.Start();
    }

    /// <summary>대화형 — ConsoleIO 줄 에디터 (로그 침범 방지 + ↑↓ 히스토리).
    /// 완성된 라인만 _pendingLines 큐에 넣는다 (디스패치는 Tick이 담당).</summary>
    private void ReadStdinLoopInteractive()
    {
        ConsoleIO.ShowPrompt();

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"stdin 읽기 실패, 운영 콘솔 스레드 종료 — {ex.Message}");
                return;
            }

            // Ctrl+C — 대화형 ReadKey가 SIGINT를 키로 소비하므로 직접 graceful 종료를 요청한다
            // (CancelKeyPress는 비대화형 경로에서만 유효).
            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                ConsoleIO.WriteLine("종료 신호 수신 — graceful 종료 시작.");
                ShutdownRequested?.Invoke();
                return;
            }

            string? line = ConsoleIO.HandleKey(key);
            if (line != null && !string.IsNullOrWhiteSpace(line))
            {
                _pendingLines.Enqueue(line);
            }
        }
    }

    /// <summary>비대화형 폴백 (stdin 리다이렉트 — 파이프/테스트).</summary>
    private void ReadStdinLoopBlocking()
    {
        while (true)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"stdin 읽기 실패, 운영 콘솔 스레드 종료 — {ex.Message}");
                return;
            }
            if (line == null) return;
            if (!string.IsNullOrWhiteSpace(line))
            {
                _pendingLines.Enqueue(line.Trim());
            }
        }
    }

    public void Tick()
    {
        while (_pendingLines.TryDequeue(out string? line))
        {
            Execute(line);
        }
    }

    /// <summary>외부 소스(콘솔 채널 — Discord 원격 명령)의 명령 라인을 접수한다.
    /// _pendingLines 큐(ConcurrentQueue)로 넣으면 메인 루프의 Tick이 stdin 입력과
    /// 동일하게 Execute로 실행한다 — 터미널 입력과 완전히 동일한 경로.</summary>
    internal void SubmitLine(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _pendingLines.Enqueue(line.Trim());
        }
    }

    private void Execute(string line)
    {
        string[] argv = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0) return;
        if (!ConsoleCommands.TryExecute(argv, out string[]? lines))
        {
            Console.WriteLine($"알 수 없는 명령: {argv[0]}");
            return;
        }
        if (lines != null)
        {
            foreach (string l in lines) Console.WriteLine(l);
        }
    }

    // ── 등록 핸들러 (복수 출력 라인 반환형) ──

    /// <summary>instance [reset|stop <key|depth> | spawn <depth>] — 인스턴스 상태/조작.</summary>
    private string[]? HandleInstance(string[] argv)
    {
        string subCmd = argv.Length > 1 ? argv[1].ToLowerInvariant() : "";
        string subArg = string.Join(" ", argv.Skip(2));

        if (subCmd.Length == 0)
        {
            // 실행 중(Ready/Idle) 인스턴스만 표시 — 정지/크래시 기록은 제외. depth 순 정렬.
            var running = _instances.All
                .Where(s => s.Status is InstanceStatus.Ready or InstanceStatus.Idle)
                .OrderBy(s => s.Depth)
                .ToList();
            var lines = new List<string> { $"총 인스턴스 {running.Count}개 :" };
            foreach (InstanceInfo i in running)
            {
                string addr = _instances.BackendAddrFor(i) ?? "-";
                lines.Add($"{i.Key} — depth {i.Depth}, 포트 {i.Port}, 머신 {i.MachineId ?? "-"}, {i.Status}, 주소 {addr}");
            }
            return lines.ToArray();
        }

        switch (subCmd)
        {
            case "reset":
            case "stop":
                string? key = ResolveInstanceKey(subArg);
                if (key == null || _instances.Find(key) == null)
                {
                    return new[] { $"인스턴스를 찾을 수 없습니다: {subArg}" };
                }
                if (subCmd == "reset")
                {
                    _instances.ResetInstance(key);
                    return null;
                }
                InstanceInfo? stopInfo = _instances.Find(key);
                _instances.StopInstance(key);
                if (stopInfo != null && _instances.IsPrewarmDepth(stopInfo.Depth))
                {
                    // Prewarm 인스턴스 — 종료 후 자동 재시작 (유지 보수 용도).
                    _instances.SchedulePrewarmRestart(key, TimeSpan.FromSeconds(5));
                    return new[] { $"{key}은(는) Prewarm 인스턴스 — 종료 후 5초 뒤 자동 재시작 예약." };
                }
                return null;
            case "spawn":
                if (!int.TryParse(subArg, out int spawnDepth) || spawnDepth < 1)
                {
                    return new[] { "사용법: instance spawn <depth>  (예: instance spawn 4)" };
                }
                if (_instances.IsPrewarmDepth(spawnDepth))
                {
                    return new[] { $"depth-{spawnDepth}은(는) Prewarm 레이어 — instance spawn으로 생성할 수 없습니다 (자동 프리웜/재시작이 관리)." };
                }
                string? spawnedAddr = _instances.EnsureInstance(spawnDepth);
                return new[] { spawnedAddr != null
                    ? $"depth-{spawnDepth} 준비 (주소 {spawnedAddr})."
                    : $"depth-{spawnDepth} 스폰 실패 (에이전트 없음/수용량 초과)." };
            default:
                return new[] { $"알 수 없는 instance 액션: {subCmd} — reset|stop <key|depth>, spawn <depth>" };
        }
    }

    /// <summary>connections — 연결된 게이트웨이/에이전트/인스턴스(모드) 목록.</summary>
    private string[]? HandleConnections()
    {
        var conns = _hub.Connections.Where(c => !c.Closed).ToList();
        if (conns.Count == 0) return new[] { "연결된 프로세스가 없습니다." };

        var lines = new List<string>();
        foreach (ControlHub.ClientConnection c in conns)
        {
            string remote = c.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep
                ? $"{ep.Address}:{ep.Port}" : "-";
            string label = c.Kind switch
            {
                ClientKind.Gateway => $"Gateway  v{c.GatewayVersion}",
                ClientKind.Agent => $"Agent    {c.MachineId} (수용 {c.AgentCapacity}, {c.AgentAddress})",
                ClientKind.Mod => $"Instance {c.InstanceKey} (포트 {c.InstancePort}, depth {c.InstanceDepth})",
                _ => $"Unknown  conn #{c.Id}",
            };
            lines.Add($"  [{label}] {remote}");
        }
        return lines.ToArray();
    }

    /// <summary>prewarm [set <agent>|reset] — Prewarm 선호 에이전트 지정/해제.</summary>
    private string[]? HandlePrewarm(string[] argv)
    {
        string psub = argv.Length > 1 ? argv[1].ToLowerInvariant() : "";
        switch (psub)
        {
            case "set":
                if (argv.Length < 3)
                {
                    return new[] { "사용법: prewarm set <agent>  (예: prewarm set m1)" };
                }
                _instances.SetPreferredPrewarmAgent(argv[2]);
                return new[] { $"Prewarm 선호 에이전트: {argv[2]} (미연결/포화 시 알고리즘 폴백)." };
            case "reset":
                _instances.SetPreferredPrewarmAgent(null);
                return new[] { "Prewarm 선호 에이전트 해제 — 알고리즘 배치 사용." };
            default:
                string? pref = _instances.PreferredPrewarmAgent;
                return new[] { pref != null
                    ? $"Prewarm 선호 에이전트: {pref}"
                    : "Prewarm 선호 에이전트: 없음 (알고리즘 배치)." };
        }
    }

    /// <summary>migrate <player> [targetLayer] — 수동 마이그레이션.</summary>
    private string[]? HandleMigrate(string[] argv)
    {
        if (argv.Length < 2)
        {
            return new[] { "사용법: migrate <player> [targetLayer]  (예: migrate player2222 3)" };
        }
        if (!TryResolvePlayer(argv[1], out PlayerKey migrateKey))
        {
            return null; // TryResolvePlayer가 실패 사유를 출력
        }
        PlayerState? mstate = _sessions.Get(migrateKey);
        if (mstate == null)
        {
            return new[] { "플레이어를 찾을 수 없습니다." };
        }
        int migrateTarget;
        if (argv.Length > 2)
        {
            if (!int.TryParse(argv[2], out migrateTarget))
            {
                return new[] { $"유효하지 않은 목적지 레이어: {argv[2]}" };
            }
        }
        else
        {
            migrateTarget = _migrations.NextLayerDepth(mstate.Depth);
        }
        string? migrateResult = _migrations.ManualMigrate(migrateKey, migrateTarget);
        return migrateResult != null
            ? new[] { migrateResult }
            : new[] { $"{mstate.Username ?? migrateKey.Value}: L{mstate.Depth} → L{migrateTarget} 마이그레이션 시작." };
    }

    /// <summary>구 시스템 의미 복원 — 매개변수 전체를 게임 콘솔 명령으로 보고
    /// 지금 떠 있는 모든 인스턴스에 실행시킨다 (예: exec kill player2222).
    /// 플레이어가 없는 인스턴스는 게임 콘솔이 no-op 처리한다.</summary>
    private void HandleRun(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.WriteLine("실행할 명령이 비어있습니다. 예: exec kill player2222");
            return;
        }
        if (_consoleRelay == null)
        {
            Console.WriteLine("콘솔 릴레이가 연결되지 않았습니다.");
            return;
        }
        _consoleRelay(command.Trim());
    }

    /// <summary>rule.json/run.json 설정 변경 — `rule <이름> <값>` / `run <설정> <값>`.
    /// 존재하지 않는 키는 거부하고, 값 변환 규칙:
    /// ① 기존 저장값이 boolean 형식("True"/"False")이고 입력이 1/0이면 True/False로 변환.
    /// ② 그 외(숫자 형식 필드, "1.0"/"0.0" 등)는 문자열 그대로 저장.
    /// 성공 시 파일 재기록 + 전 인스턴스 RUN_RULES_STATE 푸시 (즉시 반영).</summary>
    private void HandleSet(string commandName, string arg, bool isRun)
    {
        string[] parts = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Console.WriteLine($"사용법: {commandName} <이름> <값>  (예: {commandName} ShowPlayerDirections 1)");
            return;
        }
        string key = parts[0];
        string rawValue = parts[1].Trim();

        bool exists = isRun ? _runRuleStore.ContainsRun(key) : _runRuleStore.ContainsRule(key);
        if (!exists)
        {
            Console.WriteLine($"실패: {commandName}.json에 없는 키입니다 — {key}");
            return;
        }

        string current = (isRun ? _runRuleStore.RunSnapshot : _runRuleStore.RuleSnapshot)
            .TryGetValue(key, out string? cur) ? cur : "";
        string value = ConvertValue(current, rawValue);

        if (isRun) _runRuleStore.SetRun(key, value);
        else _runRuleStore.SetRule(key, value);

        _pushRulesAction?.Invoke();
        Console.WriteLine($"{commandName} {key} = {value} 반영 (파일 + 전 인스턴스 푸시).");
    }

    /// <summary>값 변환 — 기존 저장값이 bool 형식("True"/"False")이면 1/0 → True/False.
    /// 숫자 형식 필드나 그 외 입력은 문자열 그대로 (bool 필드가 아니면 게임이 1/0을
    /// 숫자로 인식해야 하므로 변환하지 않는다).</summary>
    private static string ConvertValue(string current, string raw)
    {
        bool isBoolField = string.Equals(current, "True", StringComparison.OrdinalIgnoreCase)
            || string.Equals(current, "False", StringComparison.OrdinalIgnoreCase);
        if (isBoolField)
        {
            if (string.Equals(raw, "1", StringComparison.Ordinal)) return "True";
            if (string.Equals(raw, "0", StringComparison.Ordinal)) return "False";
        }
        return raw;
    }

    /// <summary>"depth-N" 키 또는 숫자 depth를 인스턴스 키로 변환 ("1" → "depth-1").</summary>
    private static string? ResolveInstanceKey(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        if (int.TryParse(arg, out int depth)) return InstanceManager.DepthKey(depth);
        return arg;
    }

    /// <summary>락다운 토글 — 켜면 ① 현재 접속 세션 전체 추방(bypass 제외) ② orchestrator.json의
    /// LockdownBypass(SteamID64)만 접속 허용 ③ 서버 타이틀 뒤에 (MAINTENANCE) 접미.
    /// 끄면 원상 복구. 상태는 메모리 전용 (영속화 없음).</summary>
    private void ToggleLockdown()
    {
        var gateway = _hub.GatewayConnection;
        if (gateway == null)
        {
            Console.WriteLine("게이트웨이 미연결 — lockdown 불가.");
            return;
        }

        if (!LockdownState.Active)
        {
            // orchestrator.json 재로드 — 운영자가 LockdownBypass를 편집한 뒤 실행한다.
            OrchestratorConfig fresh = _configPath.Length > 0
                ? OrchestratorConfig.Load(_configPath)
                : _config;
            ulong[] bypass = (fresh.LockdownBypass ?? new List<ulong>()).ToArray();

            LockdownState.Active = true;
            LockdownState.Bypass = bypass;
            _hub.SendNoAck(gateway, "MAINTENANCE", new
            {
                On = true,
                Message = "Server is in maintenance mode",
                KickAll = true,
                Bypass = bypass,
            });
            PushAuthInfo(gateway, " (MAINTENANCE)");
            Console.WriteLine($"락다운 켬 — 전 세션 추방, bypass {bypass.Length}명, 타이틀 접미 적용.");
            LockdownNotify?.Invoke(true);
        }
        else
        {
            LockdownState.Active = false;
            LockdownState.Bypass = Array.Empty<ulong>();
            _hub.SendNoAck(gateway, "MAINTENANCE", new { On = false, KickAll = false });
            PushAuthInfo(gateway, "");
            Console.WriteLine("락다운 끔 — 접속 허용 + 타이틀 복원.");
            LockdownNotify?.Invoke(false);
        }
    }

    /// <summary>AUTH_INFO 재푸시 — 서버명(접미 포함)/비밀번호/밴 목록/인원 게이트웨이에 전파
    /// (밴 목록은 ApplyAuthInfo가 클리어 후 재구축하므로 현재 전체를 포함해야 한다).</summary>
    private void PushAuthInfo(ControlHub.ClientConnection gateway, string suffix)
    {
        _hub.SendNoAck(gateway, "AUTH_INFO", new
        {
            serverName = _config.ServerName + suffix,
            serverPassword = _config.ServerPassword,
            bannedKeys = _banList.All,
            maxPlayers = _config.MaxPlayers,
        });
    }

    /// <summary>verbose 토글 — `verbose`만 입력하면 현재 상태에 따라 켬/끔 전환
    /// (lockdown과 동일한 토글 형태). orchestrator.json에도 영속 반영 +
    /// 게이트웨이/에이전트/모드 전 컴포넌트에 VERBOSE 재푸시 (재시작 없이 즉시 적용).</summary>
    private void ToggleVerbose()
    {
        bool next = !VerboseState.Active;
        VerboseState.Active = next;
        PersistVerbose(next);
        foreach (var conn in _hub.Connections.Where(c => !c.Closed))
        {
            _hub.SendNoAck(conn, "VERBOSE", new { on = next });
        }
        Console.WriteLine($"verbose {(next ? "켬" : "끔")} — orchestrator.json 반영 + 전 컴포넌트 전파.");
    }

    /// <summary>orchestrator.json의 Verbose 필드 영속 반영 (JsonNode로 최소 수정 — 주석/포맷 유지).</summary>
    private void PersistVerbose(bool on)
    {
        try
        {
            if (_configPath.Length == 0 || !File.Exists(_configPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            var node = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText());
            node!["Verbose"] = on;
            File.WriteAllText(_configPath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"verbose json 반영 실패: {ex.Message}");
        }
    }

    private bool TryResolvePlayer(string input, out PlayerKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(input)) return false;
        if (input.StartsWith("STEAM_") || input.StartsWith("NAME_"))
        {
            key = PlayerKey.FromString(input);
            return _sessions.Get(key) != null;
        }
        // 유저명 또는 steamId 검색
        PlayerState? byName = _sessions.All.FirstOrDefault(p => p.Key.Value == $"NAME_{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(input))}");
        PlayerState? bySteam = ulong.TryParse(input, out ulong sid) ? _sessions.Get(PlayerKey.FromSteamId(sid)) : null;
        PlayerState? found = byName ?? bySteam;
        if (found != null)
        {
            key = found.Key;
            return true;
        }
        Console.WriteLine($"플레이어를 찾지 못했습니다: {input}");
        return false;
    }
}
