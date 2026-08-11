using System;
using System.Collections.Generic;
using System.Linq;

namespace CasuMpOrchestrator;

/// <summary>콘솔 명령어 레지스트리 — 등록 패턴 (2026-08-08 리팩토링).
/// Register(name, description, handler)로 등록하고 help는 자동 생성된다.
/// 핸들러는 argv(명령 포함 전체 분할)를 받아 출력 라인 배열을 반환한다 (null = 출력 없음).</summary>
public sealed class ConsoleCommand
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Func<string[], string[]?> Handler { get; init; } = _ => null;
}

public static class ConsoleCommands
{
    private static readonly Dictionary<string, ConsoleCommand> Commands = new();

    /// <summary>명령어 등록 — name은 소문자 매칭, 같은 이름이면 덮어쓴다.</summary>
    public static void Register(string name, string description, Func<string[], string[]?> handler)
    {
        Commands[name] = new ConsoleCommand { Name = name, Description = description, Handler = handler };
    }

    /// <summary>명령 실행 — argv[0] = 명령어 (소문자 매칭). 성공 시 출력 라인 반환 (null = 출력 없음).</summary>
    public static bool TryExecute(string[] argv, out string[]? lines)
    {
        lines = null;
        if (argv.Length == 0) return false;
        if (!Commands.TryGetValue(argv[0].ToLowerInvariant(), out ConsoleCommand? cmd)) return false;
        lines = cmd.Handler(argv);
        return true;
    }

    /// <summary>자동 생성 도움말 — 등록된 모든 명령을 이름순 정렬.</summary>
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
