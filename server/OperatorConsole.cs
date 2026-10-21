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
        _registry.Register("connections", "연결/레이어 목록", CmdConnections);
        _registry.Register("clear", "화면 지우기", CmdClear);
        _registry.Register("migrate", "수동 마이그레이션", "<player> [targetLayer]", CmdMigrate);
        _registry.Register("freeze", "레이어 동결", "<layer>", CmdFreeze);
        _registry.Register("unfreeze", "레이어 동결 해제", "<layer>", CmdUnfreeze);
        _registry.Register("swap", "두 레이어의 에이전트 교체", "<layerA> <layerB>", CmdSwap);
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
        var table = new Table().Title("연결/레이어").AddColumn("종류").AddColumn("대상")
            .AddColumn("상태").AddColumn("에이전트").AddColumn("포트").AddColumn("원격");
        var discordLines = new List<string>();

        foreach (ControlHub.ClientConnection c in _hub.Connections
            .Where(c => !c.Closed && c.Kind == ClientKind.Agent)
            .OrderBy(c => c.MachineId))
        {
            int used = _instances.All.Count(i => i.MachineId == c.MachineId
                && i.Status is not (InstanceStatus.Stopped or InstanceStatus.Crashed));
            string status = $"connected ({used}/{c.AgentCapacity})";
            string remote = RemoteAddress(c);
            table.AddRow("Agent", Markup.Escape(c.MachineId ?? "-"), Markup.Escape(status),
                Markup.Escape(c.AgentAddress ?? "-"), "-", Markup.Escape(remote));
            discordLines.Add($"[Agent] {c.MachineId} {status} {c.AgentAddress} {remote}");
        }

        for (int depth = 1; depth <= InstanceManager.LayerCount; depth++)
        {
            InstanceInfo? info = _instances.FindByDepth(depth);
            string target = info == null ? $"L{depth}" : $"L{depth} ({info.Key})";
            string status = info?.Status.ToString() ?? "-";
            string machine = info?.MachineId ?? "-";
            if (info?.MachineId != null && _hub.AgentConnection(info.MachineId) == null)
                machine += " (offline)";
            string port = info?.Port.ToString() ?? "-";
            string remote = RemoteAddress(info?.ModConnection);
            table.AddRow("Layer", Markup.Escape(target), Markup.Escape(status),
                Markup.Escape(machine), port, Markup.Escape(remote));
            discordLines.Add($"[Layer] {target} {status} {machine} :{port} {remote}");
        }

        foreach (ControlHub.ClientConnection c in _hub.Connections
            .Where(c => !c.Closed && c.Kind == ClientKind.Unknown)
            .OrderBy(c => c.Id))
        {
            string remote = RemoteAddress(c);
            table.AddRow("Handshake", Markup.Escape($"conn #{c.Id}"), "waiting",
                "-", "-", Markup.Escape(remote));
            discordLines.Add($"[Handshake] conn #{c.Id} {remote}");
        }

        if (_config.SteamEnabled)
        {
            string endpoint = $"127.0.0.1:{_config.SteamBridgePort}";
            table.AddRow("Service", "Steam Bridge", "active", "-",
                _config.SteamBridgePort.ToString(), Markup.Escape(endpoint));
            discordLines.Add($"[Service] Steam Bridge active {endpoint}");
        }

        ConsoleRenderer.Write(table, discordLines);
    }

    private static string RemoteAddress(ControlHub.ClientConnection? conn) =>
        conn?.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint ep
            ? $"{ep.Address}:{ep.Port}" : "-";

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

    private void CmdFreeze(string[] args)
    {
        if (args.Length != 1 || !TryParseLayer(args[0], out int depth))
        {
            Log.Info("사용법: freeze <layer>  (예: freeze 3)");
            return;
        }

        InstanceInfo? info = _instances.FindByDepth(depth);
        if (info == null)
        {
            Log.Info($"L{depth} 인스턴스가 없습니다.");
            return;
        }
        if (_instances.IsSwapping(depth))
        {
            Log.Info($"L{depth}는 레이어 교체 진행 중입니다.");
            return;
        }
        if (info.Status is InstanceStatus.Suspended or InstanceStatus.Suspending)
        {
            Log.Info($"L{depth}는 이미 동결 상태입니다.");
            return;
        }
        if (info.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
        {
            Log.Info($"L{depth} 동결 불가: 현재 상태 {info.Status}");
            return;
        }

        int users = CountLayerUsers(info);
        if (users > 0)
        {
            Log.Info($"L{depth} 동결 불가: 접속자 {users}명");
            return;
        }

        int targeting = _migrations.CountTargeting(info.Key);
        if (targeting > 0)
        {
            Log.Info($"L{depth} 동결 불가: 마이그레이션 대상 {targeting}건");
            return;
        }

        if (_instances.SuspendInstance(info.Key, "manual"))
            Log.Info($"L{depth} 동결 요청 — 유저 접속 시 자동으로 해제됩니다.");
        else
            Log.Info($"L{depth} 동결 실패 — 에이전트/모드 연결을 확인하세요.");
    }

    private void CmdUnfreeze(string[] args)
    {
        if (args.Length != 1 || !TryParseLayer(args[0], out int depth))
        {
            Log.Info("사용법: unfreeze <layer>  (예: unfreeze 3)");
            return;
        }

        InstanceInfo? info = _instances.FindByDepth(depth);
        if (info == null)
        {
            Log.Info($"L{depth} 인스턴스가 없습니다.");
            return;
        }
        if (_instances.IsSwapping(depth))
        {
            Log.Info($"L{depth}는 레이어 교체 진행 중입니다.");
            return;
        }
        if (info.Status is InstanceStatus.Ready or InstanceStatus.Idle)
        {
            Log.Info($"L{depth}는 이미 활성 상태입니다.");
            return;
        }
        if (info.Status == InstanceStatus.Resuming)
        {
            Log.Info($"L{depth}는 이미 동결 해제 중입니다.");
            return;
        }
        if (info.Status is not (InstanceStatus.Suspended or InstanceStatus.Suspending))
        {
            Log.Info($"L{depth} 동결 해제 불가: 현재 상태 {info.Status}");
            return;
        }

        if (_instances.ResumeInstance(info.Key))
            Log.Info($"L{depth} 동결 해제 요청 — 유휴 시 {_config.IdleShutdownDelaySeconds}초 후 자동 동결됩니다.");
        else
            Log.Info($"L{depth} 동결 해제 실패 — 에이전트/모드 연결을 확인하세요.");
    }

    private void CmdSwap(string[] args)
    {
        if (args.Length != 2
            || !TryParseLayer(args[0], out int depthA)
            || !TryParseLayer(args[1], out int depthB))
        {
            Log.Info("사용법: swap <layerA> <layerB>  (예: swap 1 3)");
            return;
        }
        if (depthA == depthB)
        {
            Log.Info("서로 다른 두 레이어를 지정해야 합니다.");
            return;
        }

        InstanceInfo? a = _instances.FindByDepth(depthA);
        InstanceInfo? b = _instances.FindByDepth(depthB);
        if (a == null || b == null)
        {
            Log.Info(a == null ? $"L{depthA} 인스턴스가 없습니다." : $"L{depthB} 인스턴스가 없습니다.");
            return;
        }

        var blockers = new List<string>();
        int usersA = CountLayerUsers(a);
        int usersB = CountLayerUsers(b);
        if (usersA > 0) blockers.Add($"L{depthA} 접속자 {usersA}명");
        if (usersB > 0) blockers.Add($"L{depthB} 접속자 {usersB}명");

        int usingA = _migrations.CountUsing(a.Key);
        int usingB = _migrations.CountUsing(b.Key);
        if (usingA > 0) blockers.Add($"L{depthA} 마이그레이션 {usingA}건");
        if (usingB > 0) blockers.Add($"L{depthB} 마이그레이션 {usingB}건");

        if (blockers.Count > 0)
        {
            Log.Info($"레이어 교체 불가: {string.Join(", ", blockers)}");
            return;
        }

        string? reason = _instances.ValidateSwap(depthA, depthB);
        if (reason != null)
        {
            Log.Info($"레이어 교체 불가: {reason}");
            return;
        }

        if (!_instances.TryStartSwap(depthA, depthB, out reason))
        {
            Log.Info($"레이어 교체 실패: {reason}");
            return;
        }

        Log.Info($"L{depthA} ↔ L{depthB} 교체를 시작했습니다. " +
            "두 레이어는 재스폰되며 월드가 재생성됩니다.");
    }

    private static bool TryParseLayer(string text, out int depth)
    {
        string value = text.Trim();
        if (value.StartsWith('L') || value.StartsWith('l'))
            value = value[1..];
        return int.TryParse(value, out depth)
            && depth >= 1 && depth <= InstanceManager.LayerCount;
    }

    private int CountLayerUsers(InstanceInfo info) =>
        _sessions.All.Count(p => p.Session != PlayerSessionState.Offline
            && (p.InstanceId == info.Key
                || (p.Session == PlayerSessionState.Connecting && p.Depth == info.Depth)));

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
