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
        string[] top = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (top.Length == 0) return;
        string command = top[0].ToLowerInvariant();
        string arg = top.Length > 1 ? top[1] : "";

        switch (command)
        {
            case "help":
                Console.WriteLine("명령어 목록:");
                Console.WriteLine("help - 명령어 목록을 표시합니다");
                Console.WriteLine("list - 유저 목록을 표시합니다");
                Console.WriteLine("kick <player> - 특정 유저를 추방합니다");
                Console.WriteLine("ban <player> - 특정 유저를 차단합니다");
                Console.WriteLine("unban <player> - 특정 유저의 차단을 해제합니다");
                Console.WriteLine("instance [reset|stop <key|depth> | spawn <depth>] - 인스턴스 상태/조작");
                Console.WriteLine("connections - 연결된 게이트웨이/에이전트/인스턴스(모드) 목록");
                Console.WriteLine("prewarm [set <agent>|reset] - Prewarm 선호 에이전트 지정/해제");
                Console.WriteLine("clear - 터미널 화면을 지웁니다");
                Console.WriteLine("migrate <player> [targetLayer] - 특정 유저를 수동 마이그레이션합니다 (기본: 다음 레이어)");
                Console.WriteLine("exec <command...> - 모든 인스턴스에 게임 콘솔 명령을 실행합니다");
                Console.WriteLine("rule <이름> <값> - rule.json 수정 + 전 인스턴스 즉시 반영 (1/0 → True/False)");
                Console.WriteLine("run <설정> <값> - run.json 수정 + 전 인스턴스 즉시 반영 (1/0 → True/False)");
                break;
            case "list":
                // 온라인 플레이어만 — 레이어 오름차순, 같은 레이어는 닉네임 오름차순.
                // 한 줄 = 한 명: "{닉네임} (L{레이어}, {STEAM_키})".
                var online = _sessions.All
                    .Where(p => p.Session != PlayerSessionState.Offline)
                    .OrderBy(p => p.Depth)
                    .ThenBy(p => p.Username ?? p.Key.Value)
                    .ToList();
                if (online.Count == 0)
                {
                    Console.WriteLine("현재 총 0명 접속 중 :");
                    break;
                }
                Console.WriteLine($"현재 총 {online.Count}명 접속 중 :");
                foreach (PlayerState p in online)
                {
                    string name = p.Username ?? p.Key.Value;
                    Console.WriteLine($"{name} (L{p.Depth}, {p.Key.Value})");
                }
                break;
            case "kick":
                if (TryResolvePlayer(arg, out PlayerKey kick)) _kickAction(kick.Value, "Kicked by operator.");
                break;
            case "lockdown":
                ToggleLockdown();
                break;
            case "ban":
                if (TryResolvePlayer(arg, out PlayerKey ban)) _banAction(ban.Value, null);
                break;
            case "unban":
                if (TryResolvePlayer(arg, out PlayerKey unban)) _banAction(unban.Value, "unban");
                break;
            case "instance":
                string[] sub = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                string subCmd = sub.Length > 0 ? sub[0].ToLowerInvariant() : "";
                string subArg = sub.Length > 1 ? sub[1] : "";
                if (subCmd.Length == 0)
                {
                    // 실행 중(Ready/Idle) 인스턴스만 표시 — 정지/크래시 기록은 제외. depth 순 정렬.
                    var running = _instances.All
                        .Where(s => s.Status is InstanceStatus.Ready or InstanceStatus.Idle)
                        .OrderBy(s => s.Depth)
                        .ToList();
                    Console.WriteLine($"총 인스턴스 {running.Count}개 :");
                    foreach (InstanceInfo i in running)
                    {
                        string addr = _instances.BackendAddrFor(i) ?? "-";
                        Console.WriteLine($"{i.Key} — depth {i.Depth}, 포트 {i.Port}, 머신 {i.MachineId ?? "-"}, {i.Status}, 주소 {addr}");
                    }
                    break;
                }
                switch (subCmd)
                {
                    case "reset":
                    case "stop":
                        string? key = ResolveInstanceKey(subArg);
                        if (key == null || _instances.Find(key) == null)
                        {
                            Console.WriteLine($"인스턴스를 찾을 수 없습니다: {subArg}");
                            break;
                        }
                        if (subCmd == "reset")
                        {
                            _instances.ResetInstance(key);
                        }
                        else
                        {
                            InstanceInfo? stopInfo = _instances.Find(key);
                            _instances.StopInstance(key);
                            if (stopInfo != null && _instances.IsPrewarmDepth(stopInfo.Depth))
                            {
                                // Prewarm 인스턴스 — 종료 후 자동 재시작 (유지 보수 용도).
                                _instances.SchedulePrewarmRestart(key, TimeSpan.FromSeconds(5));
                                Console.WriteLine($"{key}은(는) Prewarm 인스턴스 — 종료 후 5초 뒤 자동 재시작 예약.");
                            }
                        }
                        break;
                    case "spawn":
                        if (!int.TryParse(subArg, out int spawnDepth) || spawnDepth < 1)
                        {
                            Console.WriteLine("사용법: instance spawn <depth>  (예: instance spawn 4)");
                            break;
                        }
                        if (_instances.IsPrewarmDepth(spawnDepth))
                        {
                            Console.WriteLine($"depth-{spawnDepth}은(는) Prewarm 레이어 — instance spawn으로 생성할 수 없습니다 (자동 프리웜/재시작이 관리).");
                            break;
                        }
                        string? spawnedAddr = _instances.EnsureInstance(spawnDepth);
                        Console.WriteLine(spawnedAddr != null
                            ? $"depth-{spawnDepth} 준비 (주소 {spawnedAddr})."
                            : $"depth-{spawnDepth} 스폰 실패 (에이전트 없음/수용량 초과).");
                        break;
                    default:
                        Console.WriteLine($"알 수 없는 instance 액션: {subCmd} — reset|stop <key|depth>, spawn <depth>");
                        break;
                }
                break;
            case "clear":
                ConsoleIO.ClearScreen();
                break;
            case "connections":
                var conns = _hub.Connections.Where(c => !c.Closed).ToList();
                if (conns.Count == 0)
                {
                    Console.WriteLine("연결된 프로세스가 없습니다.");
                    break;
                }
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
                    Console.WriteLine($"  [{label}] {remote}");
                }
                break;
            case "prewarm":
                string[] pargs = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                string psub = pargs.Length > 0 ? pargs[0].ToLowerInvariant() : "";
                switch (psub)
                {
                    case "set":
                        if (pargs.Length < 2)
                        {
                            Console.WriteLine("사용법: prewarm set <agent>  (예: prewarm set m1)");
                            break;
                        }
                        _instances.SetPreferredPrewarmAgent(pargs[1]);
                        Console.WriteLine($"Prewarm 선호 에이전트: {pargs[1]} (미연결/포화 시 알고리즘 폴백).");
                        break;
                    case "reset":
                        _instances.SetPreferredPrewarmAgent(null);
                        Console.WriteLine("Prewarm 선호 에이전트 해제 — 알고리즘 배치 사용.");
                        break;
                    default:
                        string? pref = _instances.PreferredPrewarmAgent;
                        Console.WriteLine(pref != null
                            ? $"Prewarm 선호 에이전트: {pref}"
                            : "Prewarm 선호 에이전트: 없음 (알고리즘 배치).");
                        break;
                }
                break;
            case "migrate":
                string[] mparts = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (mparts.Length == 0)
                {
                    Console.WriteLine("사용법: migrate <player> [targetLayer]  (예: migrate player2222 3)");
                    break;
                }
                if (!TryResolvePlayer(mparts[0], out PlayerKey migrateKey))
                {
                    break; // TryResolvePlayer가 실패 사유를 출력
                }
                PlayerState? mstate = _sessions.Get(migrateKey);
                if (mstate == null)
                {
                    Console.WriteLine("플레이어를 찾을 수 없습니다.");
                    break;
                }
                int migrateTarget;
                if (mparts.Length > 1)
                {
                    if (!int.TryParse(mparts[1], out migrateTarget))
                    {
                        Console.WriteLine($"유효하지 않은 목적지 레이어: {mparts[1]}");
                        break;
                    }
                }
                else
                {
                    migrateTarget = _migrations.NextLayerDepth(mstate.Depth);
                }
                string? migrateResult = _migrations.ManualMigrate(migrateKey, migrateTarget);
                if (migrateResult != null)
                {
                    Console.WriteLine(migrateResult);
                }
                else
                {
                    Console.WriteLine($"{mstate.Username ?? migrateKey.Value}: L{mstate.Depth} → L{migrateTarget} 마이그레이션 시작.");
                }
                break;
            case "exec":
                HandleRun(arg);
                break;
            case "rule":
                HandleSet("rule", arg, isRun: false);
                break;
            case "run":
                HandleSet("run", arg, isRun: true);
                break;
            default:
                Console.WriteLine($"알 수 없는 명령: {command}");
                break;
        }
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
        }
        else
        {
            LockdownState.Active = false;
            LockdownState.Bypass = Array.Empty<ulong>();
            _hub.SendNoAck(gateway, "MAINTENANCE", new { On = false, KickAll = false });
            PushAuthInfo(gateway, "");
            Console.WriteLine("락다운 끔 — 접속 허용 + 타이틀 복원.");
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
