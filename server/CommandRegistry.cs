namespace CasuMpServer;

public sealed record ConsoleCommand(
    string Name,
    string Description,
    string Usage,
    Action<string[]> Handler);

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
