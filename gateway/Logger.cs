using System.Collections.Concurrent;

namespace CasuMpGateway;

// 오케스트레이터 로그 릴레이 - bounded 큐에 적재, 연결 수립 시 flush (연결 전 유실 방지)
// 포화 시 오래된 항목부터 드랍 (로그 플러드가 제어 메시지 큐를 막지 않게 분리)
public static class Logger
{
    private const int MaxQueued = 4096;

    private static readonly ConcurrentQueue<ControlMessage> _logs = new();
    private static int _queued;
    private static long _dropped;
    private static string _source = "gateway";

    // source 확정 (Program.cs 시작 시 호출 - "gateway")
    public static void Init(string source) => _source = source;

    // 로그 전송 - suffix는 하위 표시 단위 (표시 "[gateway/sub]")
    public static void Info(string message, string? suffix = null)
    {
        string source = suffix == null ? _source : $"{_source}/{suffix}";
        Enqueue(message, source);
    }

    // 디버그 로그 표시 여부 - 오케스트레이터 VERBOSE로 설정
    public static bool Verbose;

    public static void Debug(string message, string? suffix = null)
    {
        if (Verbose) Info(message, suffix);
    }

    // 콘솔 전용 - 릴레이 없음 (연결 오류 등 오케스트레이터 콘솔 도배 방지)
    public static void Local(string message) => System.Console.WriteLine(message);

    private static void Enqueue(string message, string source)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            // 포화 - 오래된 항목 드랍 (실시간성 우선)
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

    // WriteLoop 전용 - 연결 중일 때만 소비된다 (미연결 로그는 버퍼링)
    public static bool TryDequeue(out ControlMessage? msg)
    {
        if (!_logs.TryDequeue(out msg)) return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }
}

// 디버그 로그 표시 상태 (VERBOSE 메시지 - `verbose on/off` 명령)
public sealed class VerbosePayload
{
    public bool On { get; set; }
}
