using System.Collections.Concurrent;

namespace CasuMpAgent;

// 오케스트레이터 로그 릴레이
// 게임 stdout 로그까지 합쳐서 전송, 포화 시 오래된 항목부터 드랍 (로그가 보고/ACK 큐를 막지 않게 분리)
public static class Logger
{
    private const int MaxQueued = 4096;

    private static readonly ConcurrentQueue<ControlMessage> _logs = new();
    private static int _queued;
    private static long _dropped;
    private static string _source = "agent";

    public static bool Verbose;

    public static void Init(string machineId) => _source = $"agent:{machineId}";

    public static void Info(string message, string? sourceSuffix = null)
    {
        string source = sourceSuffix == null ? _source : $"{_source}/{sourceSuffix}";
        Enqueue(message, source);
    }

    public static void Debug(string message, string? sourceSuffix = null)
    {
        if (Verbose) Info(message, sourceSuffix);
    }

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
