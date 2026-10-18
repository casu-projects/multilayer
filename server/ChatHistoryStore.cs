namespace CasuMpServer;

public sealed class ChatHistoryStore
{
    private const int MaxMessages = 200;
    private readonly object _lock = new();
    private readonly List<ChatHistoryMessage> _messages = new();
    private long _nextId;

    public ChatHistoryMessage Add(string source, string speaker, string layer, string color, string text)
    {
        lock (_lock)
        {
            var message = new ChatHistoryMessage
            {
                Id = ++_nextId,
                At = DateTime.UtcNow,
                Source = source,
                Speaker = speaker ?? "",
                Layer = layer ?? "",
                Color = color ?? "",
                Text = text ?? "",
            };
            _messages.Add(message);
            if (_messages.Count > MaxMessages)
                _messages.RemoveRange(0, _messages.Count - MaxMessages);
            return message;
        }
    }

    public ChatHistoryResponse GetSince(long since)
    {
        lock (_lock)
        {
            long lastId = _messages.Count > 0 ? _messages[^1].Id : since;
            return new ChatHistoryResponse
            {
                LastId = lastId,
                Messages = _messages.Where(message => message.Id > since).ToList(),
            };
        }
    }
}
