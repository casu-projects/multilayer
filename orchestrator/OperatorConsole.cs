using System.Collections.Concurrent;
using System.Text.Json;

namespace CasuMpOrchestrator;

/// <summary>운영자 콘솔 — stdin 명령 (G-7 확장).
/// `run <command...>` — 매개변수 전체를 게임 콘솔 명령으로 전 인스턴스에 릴레이
/// (구 시스템 의미 복원 — 설정 변경이 아니라 게임 명령 실행).</summary>
public sealed class OperatorConsole
{
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly PlayerSessionStore _sessions;
    private readonly InstanceManager _instances;
    private readonly MigrationCoordinator _migrations;
    private readonly Action<string, string?> _banAction;   // (playerKey, reason)
    private readonly Action<string, string?> _kickAction;
    private readonly Action<string>? _consoleRelay;   // (game command) — 전 인스턴스 게임 콘솔 실행

    public OperatorConsole(PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, Action<string, string?> banAction, Action<string, string?> kickAction,
        Action<string>? consoleRelay = null)
    {
        _sessions = sessions;
        _instances = instances;
        _migrations = migrations;
        _banAction = banAction;
        _kickAction = kickAction;
        _consoleRelay = consoleRelay;
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
                Console.WriteLine("instance - 인스턴스 상태를 표시합니다");
                Console.WriteLine("clear - 터미널 화면을 지웁니다");
                Console.WriteLine("migrate <player> - 특정 유저를 수동 마이그레이션합니다 (구현 예정)");
                Console.WriteLine("run <command...> - 모든 인스턴스에 게임 콘솔 명령을 실행합니다");
                break;
            case "list":
                foreach (PlayerState p in _sessions.All)
                    Console.WriteLine($"  {p.Key} — depth {p.Depth}, clientId {p.ClientId}, {p.Session}");
                break;
            case "kick":
                if (TryResolvePlayer(arg, out PlayerKey kick)) _kickAction(kick.Value, "Kicked by operator.");
                break;
            case "ban":
                if (TryResolvePlayer(arg, out PlayerKey ban)) _banAction(ban.Value, null);
                break;
            case "unban":
                if (TryResolvePlayer(arg, out PlayerKey unban)) _banAction(unban.Value, "unban");
                break;
            case "instance":
                foreach (InstanceInfo i in _instances.All)
                {
                    string addr = _instances.BackendAddrFor(i) ?? "-";
                    Console.WriteLine($"  {i.Key} — depth {i.Depth}, 포트 {i.Port}, 머신 {i.MachineId ?? "-"}, {i.Status}, 주소 {addr}");
                }
                break;
            case "clear":
                ConsoleIO.ClearScreen();
                break;
            case "migrate":
                if (TryResolvePlayer(arg, out PlayerKey migrate))
                    Console.WriteLine("수동 마이그레이션은 구현 예정 — LAYER_END 이벤트로만 동작.");
                break;
            case "run":
                HandleRun(arg);
                break;
            default:
                Console.WriteLine($"알 수 없는 명령: {command}");
                break;
        }
    }

    /// <summary>구 시스템 의미 복원 — 매개변수 전체를 게임 콘솔 명령으로 보고
    /// 지금 떠 있는 모든 인스턴스에 실행시킨다 (예: run kill player2222).
    /// 플레이어가 없는 인스턴스는 게임 콘솔이 no-op 처리한다.</summary>
    private void HandleRun(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.WriteLine("실행할 명령이 비어있습니다. 예: run kill player2222");
            return;
        }
        if (_consoleRelay == null)
        {
            Console.WriteLine("콘솔 릴레이가 연결되지 않았습니다.");
            return;
        }
        _consoleRelay(command.Trim());
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
