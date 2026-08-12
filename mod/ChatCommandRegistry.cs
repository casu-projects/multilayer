using System;
using System.Collections.Generic;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 등록된 인게임 채팅 명령 정의 - "!" 접두사 라우터(Chat_CommandRouterPatch)가
// 레지스트리에서 찾아 Handler로 디스패치한다. Usage는 !help 자동 생성에 쓰인다.
public sealed class ChatCommand
{
    public ChatCommand(string name, string description, string usage, Action<NetPlayer, string[]> handler)
    {
        Name = name;
        Description = description;
        Usage = usage;
        Handler = handler;
    }

    public string Name { get; }
    public string Description { get; }
    public string Usage { get; }
    public Action<NetPlayer, string[]> Handler { get; }
}

// 채팅 명령 레지스트리 - 이름 대소문자 무시, !help 자동 생성(All) 지원.
// ChatCommands의 하드코딩 TryHandle 체인을 대체한다.
public sealed class ChatCommandRegistry
{
    private readonly Dictionary<string, ChatCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, string description, string usage, Action<NetPlayer, string[]> handler)
    {
        _commands[name] = new ChatCommand(name, description, usage, handler);
    }

    public void Register(string name, string description, Action<NetPlayer, string[]> handler)
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
