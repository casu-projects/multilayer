using System;
using System.Collections.Generic;
using Together;

namespace CasuMod;

public sealed class ChatCommand
{
    public ChatCommand(string name, string description, string usage, Action<ScavPlayer, string[]> handler)
    {
        Name = name;
        Description = description;
        Usage = usage;
        Handler = handler;
    }

    public string Name { get; }
    public string Description { get; }
    public string Usage { get; }
    public Action<ScavPlayer, string[]> Handler { get; }
}

public sealed class ChatCommandRegistry
{
    private readonly Dictionary<string, ChatCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, string description, string usage, Action<ScavPlayer, string[]> handler)
    {
        _commands[name] = new ChatCommand(name, description, usage, handler);
    }

    public void Register(string name, string description, Action<ScavPlayer, string[]> handler)
    {
        Register(name, description, "", handler);
    }

    public bool TryGet(string name, out ChatCommand command)
    {
        if (_commands.TryGetValue(name, out ChatCommand found))
        {
            command = found;
            return true;
        }
        command = null;
        return false;
    }

    public IReadOnlyCollection<ChatCommand> All => _commands.Values;
}
