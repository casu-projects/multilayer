using CasuMp.Protocol;

namespace CasuMpServer;

internal sealed class WebChatService
{
    private const string WebColor = "#3FB950";
    private const string CommandColor = "#79C0FF";

    private readonly ChatHistoryStore _history;
    private readonly ControlHub _hub;
    private readonly DiscordBot _discord;
    private readonly OperatorConsole _console;

    public WebChatService(ChatHistoryStore history, ControlHub hub, DiscordBot discord, OperatorConsole console)
    {
        _history = history;
        _hub = hub;
        _discord = discord;
        _console = console;
    }

    internal IReadOnlyList<ChatCommandInfo> GetCommands() =>
        _console.GetCommands()
            .Select(command => new ChatCommandInfo
            {
                Name = command.Name,
                Usage = command.Usage,
                Description = command.Description,
            })
            .ToList();

    internal async Task<ChatHistoryMessage?> ExecuteCommandAsync(string command)
    {
        string line = (command ?? "").Trim();
        if (line.StartsWith('/')) line = line[1..];
        if (line.Length == 0) return null;
        string output = await _console.ExecuteForWebAsync(line);
        if (string.IsNullOrWhiteSpace(output)) output = "명령 실행됨.";
        return _history.Add("command", "", "", CommandColor, $"> /{line}\n{output.Trim()}");
    }

    public ChatHistoryMessage? Send(string text)
    {
        string message = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0) return null;
        if (message.Length > 150) message = message[..150];

        if (message.StartsWith('/')) return null;
        ChatHistoryMessage entry = _history.Add("web", "WEB", "", WebColor, message);
        foreach (ControlHub.ClientConnection conn in _hub.Connections
            .Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            _hub.SendNoAck(conn, MessageTypes.Chat, new ChatPayload
            {
                Speaker = "WEB",
                Message = message,
                Color = WebColor,
                Web = true,
            });
        }
        _ = _discord.SendWebChatAsync(message);
        return entry;
    }
}
