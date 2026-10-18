using System.Collections.Concurrent;

namespace CasuMpGateway;

public static class Logger
{
    private const int MaxQueued = 4096;

    private static readonly ConcurrentQueue<ControlMessage> _logs = new();
    private static int _queued;
    private static long _dropped;
    private static string _source = "gateway";

    public static void Init(string source) => _source = source;

    public static void Info(string message, string? suffix = null)
    {
        string source = suffix == null ? _source : $"{_source}/{suffix}";
        Enqueue(message, source);
    }

    public static void Local(string message) => System.Console.WriteLine(message);

    private static void Enqueue(string message, string source)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            _logs.TryDequeue(out _);
            Interlocked.Decrement(ref _queued);
            long dropped = Interlocked.Increment(ref _dropped);
            if (dropped == 1 || dropped % 5000 == 0)
            {
                EnqueueDirect($"로그 버퍼 포화 — {dropped}개 드랍 (실시간성 유지).", _source);
            }
        }
        EnqueueDirect(message, source);
    }

    private static void EnqueueDirect(string message, string source) =>
        _logs.Enqueue(ControlMessage.Create(0, "LOG", new { source, message }));

    public static bool TryDequeue(out ControlMessage? msg)
    {
        if (!_logs.TryDequeue(out msg)) return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }
}
