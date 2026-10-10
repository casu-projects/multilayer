using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;

namespace CasuMpOrchestrator;

// 락다운 활성 상태 - 메모리 전용 (영속화 없음 - 오케스트레이터 재시작 시 해제)
// 게이트웨이 재연결(GATEWAY_HELLO) 시 상태 재푸시용으로 Program이 참조한다
public static class LockdownState
{
    public static bool Active;
    public static ulong[] Bypass = Array.Empty<ulong>();
}

// 디버그 로그 표시 상태 - `verbose on/off` 명령이 설정 + orchestrator.json 영속화
// 게이트웨이/에이전트/모드에 VERBOSE 메시지로 전파된다 (오케스트레이터 자체도 게이트)
public static class VerboseState
{
    public static bool Active;

    // 디버그급 콘솔 출력 - verbose=false면 숨김
    public static void Line(string message)
    {
        if (Active) Console.WriteLine(message);
    }
}

// 운영자 콘솔 - stdin 명령, CommandRegistry 등록 방식 (help 자동 생성)
// exec: 매개변수 전체를 게임 콘솔 명령으로 전 인스턴스에 릴레이 / rule·run: json 수정 +
// 전 인스턴스 실시간 반영 / lockdown: 점검 모드 토글
public sealed class OperatorConsole
{
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly ControlHub _hub;
    private readonly PlayerSessionStore _sessions;
    private readonly InstanceManager _instances;
    private readonly MigrationCoordinator _migrations;
    private readonly Action<string, string?> _banAction;   // (playerKey, reason)
    private readonly Action<string, string?> _kickAction;
    private readonly Action<string>? _consoleRelay;   // (game command) - 전 인스턴스 게임 콘솔 실행
    private readonly RunRuleStore _runRuleStore;      // rule.json/run.json 정본
    private readonly Action? _pushRulesAction;        // 변경 후 전 인스턴스 RUN_RULES_STATE 푸시
    private readonly OrchestratorConfig _config;
    private readonly BanList _banList;
    private readonly string _configPath;
    private readonly CommandRegistry _registry = new();

    // 종료 신호 수신 시 호출 (Program.Main에서 cts.Cancel과 연결 - 대화형 Ctrl+C용)
    internal Action? ShutdownRequested;

    // 락다운 시작/종료 알림 (Program이 DiscordBot.SendLockdownAsync로 연결 - true=시작)
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

    // 외부 컴포넌트(예: DiscordBot)용 명령 등록 - 콘솔 입력과 동일하게 디스패치된다
    public void Register(string name, string description, string usage, Action<string[]> handler)
    {
        _registry.Register(name, description, usage, handler);
    }

    // 내장 명령 등록 - help/usage는 레지스트리에서 자동 생성된다
    private void RegisterCommands()
    {
        _registry.Register("help", "명령어 목록", CmdHelp);
        _registry.Register("list", "유저 목록", CmdList);
        _registry.Register("kick", "유저 추방", "<player>", CmdKick);
        _registry.Register("ban", "유저 차단", "<player>", CmdBan);
        _registry.Register("unban", "차단 해제", "<player>", CmdUnban);
        _registry.Register("lockdown", "점검 모드 토글", CmdLockdown);
        _registry.Register("verbose", "디버그 로그 토글", CmdVerbose);
        _registry.Register("prewarm", "레이어1 무접속 자동 종료 토글", CmdPrewarm);
        _registry.Register("connections", "연결 목록", CmdConnections);
        _registry.Register("clear", "화면 지우기", CmdClear);
        _registry.Register("migrate", "수동 마이그레이션", "<player> [targetLayer]", CmdMigrate);
        _registry.Register("exec", "전 인스턴스 콘솔 명령 실행", "<command...>", CmdExec);
        _registry.Register("rule", "rule 업데이트", "<이름> <값>", CmdRule);
        _registry.Register("run", "run 업데이트", "<설정> <값>", CmdRun);
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

    // 대화형 - ConsoleIO 줄 에디터 (로그 침범 방지 + ↑↓ 히스토리)
    // 완성된 라인만 _pendingLines 큐에 넣는다 (디스패치는 Tick이 담당)
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

            // Ctrl+C - 대화형 ReadKey가 SIGINT를 키로 소비하므로 직접 graceful 종료를 요청한다
            // (CancelKeyPress는 비대화형 경로에서만 유효)
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

    // 비대화형 폴백 (stdin 리다이렉트 - 파이프/테스트)
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

    // 외부 소스(콘솔 채널 - Discord 원격 명령)의 명령 라인을 접수한다
    // _pendingLines 큐(ConcurrentQueue)로 넣으면 메인 루프의 Tick이 stdin 입력과
    // 동일하게 Execute로 실행한다 - 터미널 입력과 완전히 동일한 경로
    internal void SubmitLine(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _pendingLines.Enqueue(line.Trim());
        }
    }

    // 명령 디스패치 - 첫 토큰을 명령 이름으로, 나머지를 매개변수 배열로 레지스트리에 전달
    private void Execute(string line)
    {
        string[] top = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (top.Length == 0) return;
        string name = top[0].ToLowerInvariant();
        string[] args = top.Length > 1
            ? top[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        if (!_registry.TryGet(name, out ConsoleCommand? command))
        {
            Console.WriteLine($"알 수 없는 명령: {name}");
            return;
        }
        try
        {
            command.Handler(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"명령 오류 ({name}): {ex.Message}");
        }
    }

    // help - 레지스트리 기반 자동 생성 (이름순)
    private void CmdHelp(string[] args)
    {
        Console.WriteLine("명령어 목록:");
        foreach (ConsoleCommand cmd in _registry.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"{cmd.Name}{(cmd.Usage.Length > 0 ? " " + cmd.Usage : "")} - {cmd.Description}");
        }
    }

    // list - 온라인 플레이어만, 레이어 오름차순, 같은 레이어는 닉네임 오름차순
    // 한 줄 = 한 명: "{닉네임} (L{레이어}, {STEAM_키})"
    private void CmdList(string[] args)
    {
        var online = _sessions.All
            .Where(p => p.Session != PlayerSessionState.Offline)
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.Username ?? p.Key.Value)
            .ToList();
        if (online.Count == 0)
        {
            Console.WriteLine("현재 총 0명 접속 중 :");
            return;
        }
        Console.WriteLine($"현재 총 {online.Count}명 접속 중 :");
        foreach (PlayerState p in online)
        {
            string name = p.Username ?? p.Key.Value;
            Console.WriteLine($"{name} (L{p.Depth}, {p.Key.Value})");
        }
    }

    private void CmdKick(string[] args)
    {
        // 이름은 공백 포함 가능 - 원래 입력을 공백 단위로 조인해 복원한다
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey kick)) _kickAction(kick.Value, "Kicked by operator.");
    }

    private void CmdBan(string[] args)
    {
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey ban)) _banAction(ban.Value, null);
    }

    private void CmdUnban(string[] args)
    {
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey unban)) _banAction(unban.Value, "unban");
    }

    private void CmdConnections(string[] args)
    {
        var conns = _hub.Connections.Where(c => !c.Closed).ToList();
        if (conns.Count == 0)
        {
            Console.WriteLine("연결된 프로세스가 없습니다.");
            return;
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
    }

    private void CmdMigrate(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("사용법: migrate <player> [targetLayer]  (예: migrate player2222 3)");
            return;
        }
        if (!TryResolvePlayer(args[0], out PlayerKey migrateKey))
        {
            return; // TryResolvePlayer가 실패 사유를 출력
        }
        PlayerState? mstate = _sessions.Get(migrateKey);
        if (mstate == null)
        {
            Console.WriteLine("플레이어를 찾을 수 없습니다.");
            return;
        }
        int migrateTarget;
        if (args.Length > 1)
        {
            if (!int.TryParse(string.Join(" ", args.Skip(1)), out migrateTarget))
            {
                Console.WriteLine($"유효하지 않은 목적지 레이어: {args[1]}");
                return;
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
    }

    private void CmdClear(string[] args) => ConsoleIO.ClearScreen();

    private void CmdExec(string[] args) => HandleRun(string.Join(" ", args));

    private void CmdRule(string[] args) => HandleSet("rule", string.Join(" ", args), isRun: false);

    private void CmdRun(string[] args) => HandleSet("run", string.Join(" ", args), isRun: true);

    // 구 시스템 의미 복원 - 매개변수 전체를 게임 콘솔 명령으로 보고
    // 지금 떠 있는 모든 인스턴스에 실행시킨다 (예: exec kill player2222)
    // 플레이어가 없는 인스턴스는 게임 콘솔이 no-op 처리한다
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

    // rule.json/run.json 설정 변경 - `rule <이름> <값>` / `run <설정> <값>`
    // 존재하지 않는 키는 거부하고, 값 변환 규칙:
    // 기존 저장값이 boolean 형식("True"/"False")이고 입력이 1/0이면 True/False로 변환
    // 그 외(숫자 형식 필드, "1.0"/"0.0" 등)는 문자열 그대로 저장
    // 성공 시 파일 재기록 + 전 인스턴스 RUN_RULES_STATE 푸시 (즉시 반영)
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

    // 값 변환 - 기존 저장값이 bool 형식("True"/"False")이면 1/0 -> True/False
    // 숫자 형식 필드나 그 외 입력은 문자열 그대로 (bool 필드가 아니면 게임이 1/0을
    // 숫자로 인식해야 하므로 변환하지 않는다)
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

    // lockdown - 켜면 현재 접속 세션 전체 추방(bypass 제외) orchestrator.json의
    // LockdownBypass(SteamID64)만 접속 허용 서버 타이틀 뒤에 " [MNT]" 접미
    // 끄면 원상 복구. 상태는 메모리 전용 (영속화 없음)
    private void CmdLockdown(string[] args)
    {
        var gateway = _hub.GatewayConnection;
        if (gateway == null)
        {
            Console.WriteLine("게이트웨이 미연결 — lockdown 불가.");
            return;
        }

        if (!LockdownState.Active)
        {
            // orchestrator.json 재로드 - 운영자가 LockdownBypass를 편집한 뒤 실행한다
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
            PushAuthInfo(gateway, " [MNT]");
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

    // AUTH_INFO 재푸시 - 서버명(접미 포함)/비밀번호/밴 목록/인원 게이트웨이에 전파
    // (밴 목록은 ApplyAuthInfo가 클리어 후 재구축하므로 현재 전체를 포함해야 한다)
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

    // verbose - `verbose`만 입력하면 현재 상태에 따라 켬/끔 전환
    // (lockdown과 동일한 토글 형태). orchestrator.json에도 영속 반영 +
    // 게이트웨이/에이전트/모드 전 컴포넌트에 VERBOSE 재푸시 (재시작 없이 즉시 적용)
    private void CmdVerbose(string[] args)
    {
        bool next = !VerboseState.Active;
        VerboseState.Active = next;
        PersistConfigBool("Verbose", next);
        foreach (var conn in _hub.Connections.Where(c => !c.Closed))
        {
            _hub.SendNoAck(conn, "VERBOSE", new { on = next });
        }
        Console.WriteLine($"verbose {(next ? "켬" : "끔")} — orchestrator.json 반영 + 전 컴포넌트 전파.");
    }

    // prewarm - 레이어1 프리웜 "1시간 무접속 자동 종료" 토글 (verbose와 동일한 단순 토글)
    private void CmdPrewarm(string[] args)
    {
        bool next = !_instances.PrewarmIdleShutdownEnabled;
        _instances.PrewarmIdleShutdownEnabled = next;
        PersistConfigBool("PrewarmIdleShutdown", next);
        Console.WriteLine(next
            ? "prewarm idle-shutdown 켬 — 레이어1은 1시간 무접속 시 종료됩니다 (orchestrator.json 반영)."
            : "prewarm idle-shutdown 끔 — 레이어1은 무접속에도 유지됩니다(항상 프리웜) (orchestrator.json 반영).");
    }

    // orchestrator.json의 bool 필드 영속 반영 (JsonNode로 최소 수정 - 주석/포맷 유지)
    private void PersistConfigBool(string key, bool value)
    {
        try
        {
            if (_configPath.Length == 0 || !File.Exists(_configPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            var node = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText());
            node![key] = value;
            File.WriteAllText(_configPath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{key} json 반영 실패: {ex.Message}");
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
