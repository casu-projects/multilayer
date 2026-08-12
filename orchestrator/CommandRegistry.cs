namespace CasuMpOrchestrator;

// 등록된 콘솔 명령 정의 - Name(표시/찾기 이름), Usage(help 표시 규격), Handler(디스패치 대상).
public sealed record ConsoleCommand(
    string Name,
    string Description,
    string Usage,
    Action<string[]> Handler);

// 콘솔 명령 레지스트리 - 이름 대소문자 무시, help 자동 생성(All) 지원.
// OperatorConsole의 switch 디스패치를 대체하며 외부 컴포넌트(DiscordBot 등)도
// OperatorConsole.Register를 통해 명령을 추가할 수 있다.
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, string description, string usage, Action<string[]> handler)
    {
        _commands[name] = new ConsoleCommand(name, description, usage, handler);
    }

    public void Register(string name, string description, Action<string[]> handler)
    {
        Register(name, description, "", handler);
    }

    public bool TryGet(string name, out ConsoleCommand command)
    {
        return _commands.TryGetValue(name, out command!);
    }

    public IReadOnlyCollection<ConsoleCommand> All => _commands.Values;
}
