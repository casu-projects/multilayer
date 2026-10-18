namespace CasuMpServer;

public sealed class ChatCommandInfo
{
    public string Name { get; set; } = "";
    public string Usage { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ChatCommandRequest
{
    public string Command { get; set; } = "";
}
