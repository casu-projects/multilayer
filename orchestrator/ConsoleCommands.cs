using System;
using System.Collections.Generic;
using System.Linq;

namespace CasuMpOrchestrator;

// 콘솔 명령어 레지스트리 — Register로 등록하고 help는 자동 생성된다.
// 핸들러는 argv를 받아 출력 라인 배열을 반환한다 (null = 출력 없음).
public sealed class ConsoleCommand
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Func<string[], string[]?> Handler { get; init; } = _ => null;
}

public static class ConsoleCommands
{
    private static readonly Dictionary<string, ConsoleCommand> Commands = new();

    // 소문자 매칭 — 같은 이름이면 덮어쓴다.
    public static void Register(string name, string description, Func<string[], string[]?> handler)
    {
        Commands[name] = new ConsoleCommand { Name = name, Description = description, Handler = handler };
    }

    // argv[0] = 명령어. 성공 시 출력 라인 반환 (null = 출력 없음).
    public static bool TryExecute(string[] argv, out string[]? lines)
    {
        lines = null;
        if (argv.Length == 0) return false;
        if (!Commands.TryGetValue(argv[0].ToLowerInvariant(), out ConsoleCommand? cmd)) return false;
        lines = cmd.Handler(argv);
        return true;
    }

    // 등록된 모든 명령을 이름순 정렬한 도움말.
    public static string[] GetHelpLines()
    {
        var result = new List<string> { "명령어 목록:" };
        foreach (ConsoleCommand c in Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            result.Add($"{c.Name} - {c.Description}");
        }
        return result.ToArray();
    }
}
