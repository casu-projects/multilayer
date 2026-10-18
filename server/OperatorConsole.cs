using CasuMp.Protocol;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;

namespace CasuMpServer;

public static class LockdownState
{
    public static bool Active;
    public static ulong[] Bypass = Array.Empty<ulong>();
}

public sealed class OperatorConsole
{
    private readonly ConcurrentQueue<(string Line, Action<string>? Output)> _pendingLines = new();
    private readonly ControlHub _hub;
    private readonly ClientGateway _gateway;
    private readonly PlayerSessionStore _sessions;
    private readonly InstanceManager _instances;
    private readonly MigrationCoordinator _migrations;
    private readonly Action<string, string?> _banAction;   // (playerKey, reason)
    private readonly Action<string, string?> _kickAction;
    private readonly Action<string>? _consoleRelay;   // (game command) - 전 인스턴스 게임 콘솔 실행
    private readonly RunRuleStore _runRuleStore;      // rule.json/run.json 정본
    private readonly Action? _pushRulesAction;        // 변경 후 전 인스턴스 RUN_RULES_STATE 푸시
    private readonly Action? _pushLobbyAction;         // 서버명/점검 상태 변경 후 로비 메타데이터 푸시
    private readonly ServerConfig _config;
    private readonly string _configPath;
    private readonly CommandRegistry _registry = new();

    internal Action<bool>? LockdownNotify;

    public OperatorConsole(ControlHub hub, ClientGateway gateway, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, Action<string, string?> banAction, Action<string, string?> kickAction,
        RunRuleStore runRuleStore, Action? pushRulesAction = null, Action<string>? consoleRelay = null,
        Action? pushLobbyAction = null, ServerConfig? config = null, string configPath = "")
    {
        _hub = hub;
        _gateway = gateway;
        _sessions = sessions;
        _instances = instances;
        _migrations = migrations;
        _banAction = banAction;
        _kickAction = kickAction;
        _runRuleStore = runRuleStore;
        _pushRulesAction = pushRulesAction;
        _consoleRelay = consoleRelay;
        _pushLobbyAction = pushLobbyAction;
        _config = config ?? new ServerConfig();
        _configPath = configPath;
        RegisterCommands();
    }

    public void Register(string name, string description, string usage, Action<string[]> handler)
    {
        _registry.Register(name, description, usage, handler);
    }

    private void RegisterCommands()
    {
        _registry.Register("help", "명령어 목록", CmdHelp);
        _registry.Register("?", "명령어 목록", CmdHelp);
        _registry.Register("list", "유저 목록", CmdList);
        _registry.Register("kick", "유저 추방", "<player>", CmdKick);
        _registry.Register("ban", "유저 차단", "<player>", CmdBan);
        _registry.Register("unban", "차단 해제", "<player>", CmdUnban);
        _registry.Register("lockdown", "점검 모드 토글", CmdLockdown);
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
        var thread = new Thread(ReadStdinLoop)
        {
            IsBackground = true,
            Name = "Orchestrator.StdinReader",
        };
        thread.Start();
    }

    private void ReadStdinLoop()
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
                Log.Info($"stdin 읽기 실패, 운영 콘솔 스레드 종료 — {ex.Message}");
                return;
            }
            if (line == null) return;
            if (!string.IsNullOrWhiteSpace(line))
            {
                _pendingLines.Enqueue((line.Trim(), null));
            }
        }
    }

    public void Tick()
    {
        while (_pendingLines.TryDequeue(out (string Line, Action<string>? Output) item))
        {
            var captured = new List<string>();
            Action<string>? previousCapture = Log.Capture;
            if (item.Output != null)
                Log.Capture = message =>
                {
                    if (!string.IsNullOrWhiteSpace(message)) captured.Add(message);
                };
            try
            {
                Execute(item.Line);
            }
            finally
            {
                Log.Capture = previousCapture;
            }
            item.Output?.Invoke(string.Join(Environment.NewLine, captured));
        }
    }

    internal void SubmitLine(string line) => SubmitLine(line, null);

    internal void SubmitLine(string line, Action<string>? output)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _pendingLines.Enqueue((line.Trim(), output));
        }
    }

    internal IReadOnlyList<ConsoleCommand> GetCommands() =>
        _registry.All.OrderBy(command => command.Name, StringComparer.Ordinal).ToList();

    internal Task<string> ExecuteForWebAsync(string line)
    {
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SubmitLine(line, output => source.TrySetResult(output));
        return source.Task;
    }

    private void Execute(string line)
    {
        string[] tokens = CommandLineTokenizer.Split(line);
        if (tokens.Length == 0) return;
        string name = tokens[0].ToLowerInvariant();
        string[] args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        if (!_registry.TryGet(name, out ConsoleCommand? command))
        {
            Log.Info($"알 수 없는 명령: {name}");
            return;
        }
        try
        {
            command.Handler(args);
        }
        catch (Exception ex)
        {
            Log.Info($"명령 오류 ({name}): {ex.Message}"); // 명령 오류는 격리
        }
    }

    private void CmdHelp(string[] args)
    {
        var commands = _registry.All.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var table = new Table().Title("명령어").AddColumn("명령").AddColumn("사용법").AddColumn("설명");
        var discordLines = new List<string>();
        foreach (ConsoleCommand cmd in commands)
        {
            table.AddRow(
                Markup.Escape(cmd.Name),
                Markup.Escape(cmd.Usage.Length > 0 ? cmd.Usage : "-"),
                Markup.Escape(cmd.Description));
            discordLines.Add($"{cmd.Name}{(cmd.Usage.Length > 0 ? " " + cmd.Usage : "")} - {cmd.Description}");
        }
        ConsoleRenderer.Write(table, discordLines);
    }

    private void CmdList(string[] args)
    {
        var online = _sessions.All
            .Where(p => p.Session != PlayerSessionState.Offline)
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.Username ?? p.Key.Value)
            .ToList();
        if (online.Count == 0)
        {
            Log.Info("현재 접속 중인 플레이어가 없습니다.");
            return;
        }
        var table = new Table().Title($"접속 {online.Count}명")
            .AddColumn("이름").AddColumn("레이어").AddColumn("키");
        var discordLines = new List<string> { $"현재 접속 {online.Count}명" };
        foreach (PlayerState p in online)
        {
            table.AddRow(
                Markup.Escape(p.Username ?? p.Key.Value),
                $"L{p.Depth}",
                Markup.Escape(p.Key.Value));
            discordLines.Add($"{p.Username ?? p.Key.Value} (L{p.Depth}, {p.Key.Value})");
        }
        ConsoleRenderer.Write(table, discordLines);
    }

    private void CmdKick(string[] args)
    {
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey kick))
        {
            _kickAction(kick.Value, "Kicked by operator.");
            Log.Info($"추방 요청: {kick.Value}");
        }
    }

    private void CmdBan(string[] args)
    {
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey ban))
        {
            _banAction(ban.Value, null);
            Log.Info($"차단 요청: {ban.Value}");
        }
    }

    private void CmdUnban(string[] args)
    {
        if (TryResolvePlayer(string.Join(" ", args), out PlayerKey unban))
        {
            _banAction(unban.Value, "unban");
            Log.Info($"차단 해제 요청: {unban.Value}");
        }
    }

    private void CmdConnections(string[] args)
    {
        var conns = _hub.Connections.Where(c => !c.Closed).ToList();
        if (conns.Count == 0)
        {
            Log.Info("연결된 프로세스가 없습니다.");
            return;
        }
        var table = new Table().Title("연결").AddColumn("종류").AddColumn("정보").AddColumn("원격");
        var discordLines = new List<string>();
        foreach (ControlHub.ClientConnection c in conns)
        {
            string remote = c.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep
                ? $"{ep.Address}:{ep.Port}" : "-";
            string kind;
            string info;
            switch (c.Kind)
            {
                case ClientKind.Agent:
                    kind = "Agent"; info = $"{c.MachineId} (수용 {c.AgentCapacity}, {c.AgentAddress})"; break;
                case ClientKind.Mod:
                    kind = "Instance"; info = $"{c.InstanceKey} (포트 {c.InstancePort}, depth {c.InstanceDepth})"; break;
                default:
                    kind = "Unknown"; info = $"conn #{c.Id}"; break;
            }
            table.AddRow(Markup.Escape(kind), Markup.Escape(info), Markup.Escape(remote));
            discordLines.Add($"[{kind}] {info} {remote}");
        }
        var (direct, steam) = _gateway.TransportCounts;
        string clientInfo = $"Direct {direct} / Steam {steam} (총 {_gateway.SessionCount})";
        table.AddRow("Clients", Markup.Escape(clientInfo), "-");
        discordLines.Add($"[Clients] Direct {direct} / Steam {steam}");
        ConsoleRenderer.Write(table, discordLines);
    }

    private void CmdMigrate(string[] args)
    {
        if (args.Length == 0)
        {
            Log.Info("사용법: migrate <player> [targetLayer]  (예: migrate player2222 3)");
            return;
        }
        if (!TryResolvePlayer(args[0], out PlayerKey migrateKey))
        {
            return; // TryResolvePlayer가 실패 사유를 출력
        }
        PlayerState? mstate = _sessions.Get(migrateKey);
        if (mstate == null)
        {
            Log.Info("플레이어를 찾을 수 없습니다.");
            return;
        }
        int migrateTarget;
        if (args.Length > 1)
        {
            if (!int.TryParse(string.Join(" ", args.Skip(1)), out migrateTarget))
            {
                Log.Info($"유효하지 않은 목적지 레이어: {args[1]}");
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
            Log.Info(migrateResult);
        }
        else
        {
            Log.Info($"{mstate.Username ?? migrateKey.Value}: L{mstate.Depth} → L{migrateTarget} 마이그레이션 시작.");
        }
    }

    private void CmdClear(string[] args) => AnsiConsole.Clear();

    private void CmdExec(string[] args)
    {
        string command = string.Join(" ", args);
        HandleRun(command);
        if (!string.IsNullOrWhiteSpace(command))
            Log.Info($"콘솔 명령 전달: {command.Trim()}");
    }

    private void CmdRule(string[] args) => HandleSet("rule", string.Join(" ", args), isRun: false);

    private void CmdRun(string[] args) => HandleSet("run", string.Join(" ", args), isRun: true);

    private void HandleRun(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Log.Info("실행할 명령이 비어있습니다. 예: exec kill player2222");
            return;
        }
        if (_consoleRelay == null)
        {
            Log.Info("콘솔 릴레이가 연결되지 않았습니다.");
            return;
        }
        _consoleRelay(command.Trim());
    }

    private void HandleSet(string commandName, string arg, bool isRun)
    {
        string[] parts = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Log.Info($"사용법: {commandName} <이름> <값>  (예: {commandName} ShowPlayerDirections 1)");
            return;
        }
        string key = parts[0];
        string rawValue = parts[1].Trim();

        bool exists = isRun ? _runRuleStore.ContainsRun(key) : _runRuleStore.ContainsRule(key);
        if (!exists)
        {
            Log.Info($"실패: {commandName}.json에 없는 키입니다 — {key}");
            return;
        }

        string current = (isRun ? _runRuleStore.RunSnapshot : _runRuleStore.RuleSnapshot)
            .TryGetValue(key, out string? cur) ? cur : "";
        string value = ConvertValue(current, rawValue);

        if (isRun) _runRuleStore.SetRun(key, value);
        else _runRuleStore.SetRule(key, value);

        _pushRulesAction?.Invoke();
        Log.Info($"{commandName} {key} = {value} 반영 (파일 + 전 인스턴스 푸시)."); // run/rule 변경 반영
    }

    private static string ConvertValue(string current, string raw)
    {
        bool isBoolField = string.Equals(current, "True", StringComparison.OrdinalIgnoreCase) // bool 필드는 1/0 허용
            || string.Equals(current, "False", StringComparison.OrdinalIgnoreCase);
        if (isBoolField)
        {
            if (string.Equals(raw, "1", StringComparison.Ordinal)) return "True";
            if (string.Equals(raw, "0", StringComparison.Ordinal)) return "False";
        }
        return raw;
    }

    private void CmdLockdown(string[] args)
    {
        if (!LockdownState.Active)
        {
            ServerConfig fresh = _configPath.Length > 0
                ? ServerConfig.Load(_configPath)
                : _config;
            ulong[] bypass = (fresh.LockdownBypass ?? new List<ulong>()).ToArray();

            LockdownState.Active = true;
            LockdownState.Bypass = bypass; // 점검 모드: 전 세션 추방, bypass 허용
            _gateway.SetMaintenance(true, "Server is in maintenance mode", bypass, kickAll: true);
            _gateway.UpdateServerInfo(_config.ServerName + " [MNT]", _config.ServerPassword, _config.MaxPlayers);
            _pushLobbyAction?.Invoke();
            Log.Info($"락다운 켬 — 전 세션 추방, bypass {bypass.Length}명, 타이틀 접미 적용.");
            LockdownNotify?.Invoke(true);
        }
        else
        {
            LockdownState.Active = false;
            LockdownState.Bypass = Array.Empty<ulong>();
            _gateway.SetMaintenance(false, "", Array.Empty<ulong>(), kickAll: false);
            _gateway.UpdateServerInfo(_config.ServerName, _config.ServerPassword, _config.MaxPlayers);
            _pushLobbyAction?.Invoke();
            Log.Info("락다운 끔 — 접속 허용 + 타이틀 복원.");
            LockdownNotify?.Invoke(false);
        }
    }

    private void CmdPrewarm(string[] args)
    {
        bool next = !_instances.PrewarmIdleShutdownEnabled;
        _instances.PrewarmIdleShutdownEnabled = next;
        PersistConfigBool("PrewarmIdleShutdown", next);
        Log.Info(next
            ? "prewarm idle-shutdown 켬 — 레이어1은 1시간 무접속 시 종료됩니다 (server.json 반영)."
            : "prewarm idle-shutdown 끔 — 레이어1은 무접속에도 유지됩니다(항상 프리웜) (server.json 반영).");
    }

    private void PersistConfigBool(string key, bool value)
    {
        try
        {
            if (_configPath.Length == 0 || !File.Exists(_configPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            var node = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText());
            node![key] = value;
            AtomicJsonFile.Write(_configPath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Info($"{key} json 반영 실패: {ex.Message}");
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
        PlayerState? byName = _sessions.All.FirstOrDefault(p => p.Key.Value == $"NAME_{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(input))}");
        PlayerState? bySteam = ulong.TryParse(input, out ulong sid) ? _sessions.Get(PlayerKey.FromSteamId(sid)) : null;
        PlayerState? found = byName ?? bySteam;
        if (found != null)
        {
            key = found.Key;
            return true;
        }
        Log.Info($"플레이어를 찾지 못했습니다: {input}");
        return false;
    }
}
