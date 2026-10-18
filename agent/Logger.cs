using CasuMp.Protocol;
using CasuMp.Runtime;

namespace CasuMpAgent;

public static class Logger
{
    private const int MaxBuffered = 4096;

    private static readonly object _lock = new();
    private static readonly Queue<ControlMessage> _buffer = new();
    private static Action<ControlMessage>? _sink;
    private static string _source = "agent";

    public static void Init(string source) => _source = source;

    public static void SetSink(Action<ControlMessage> sink)
    {
        List<ControlMessage>? pending = null;
        lock (_lock)
        {
            _sink = sink;
            if (_buffer.Count > 0)
            {
                pending = new List<ControlMessage>(_buffer);
                _buffer.Clear();
            }
        }

        if (pending != null)
        {
            foreach (ControlMessage message in pending)
            {
                sink(message);
            }
        }
    }

    public static void Info(string message, string? suffix = null)
    {
        string source = suffix == null ? _source : $"{_source}/{suffix}";
        ControlMessage entry = ControlMessage.Create(0, MessageTypes.Log, new { source, message });

        Action<ControlMessage>? sink;
        lock (_lock)
        {
            if (_sink == null)
            {
                if (_buffer.Count >= MaxBuffered) _buffer.Dequeue();
                _buffer.Enqueue(entry);
                return;
            }
            sink = _sink;
        }
        sink(entry);
    }

    public static void Local(string message) => System.Console.WriteLine(message);
}
