using System.Collections.Concurrent;

namespace CasuMpAgent;

/// <summary>오케스트레이터 로그 릴레이 — LOG 메시지를 bounded 큐에 적재하고,
/// 연결 수립 시 WriteLoopAsync가 flush한다 (연결 전 로그 유실 방지).
/// 게임 stdout(인스턴스 로그)도 이 경로로 통합된다. 포화 시 오래된 항목부터
/// 드랍하며 실시간성을 우선한다 (게임 로그 플러드가 보고/ACK 큐를 막지 않도록 분리).</summary>
public static class AgentLog
{
    private const int MaxQueued = 4096;

    private static readonly ConcurrentQueue<ControlMessage> _logs = new();
    private static int _queued;
    private static long _dropped;
    private static string _source = "agent";

    /// <summary>머신 ID 확정 후 호출 — source = "agent:{machineId}".</summary>
    public static void Init(string machineId) => _source = $"agent:{machineId}";

    /// <summary>로그 전송 — sourceSuffix는 인스턴스 키 등 (표시 "[agent:m1/depth-1]").
    /// 메시지에는 접두사를 붙이지 않는다 (접두사는 오케스트레이터 표시 계층이 부여).</summary>
    public static void Info(string message, string? sourceSuffix = null)
    {
        string source = sourceSuffix == null ? _source : $"{_source}/{sourceSuffix}";
        Enqueue(message, source);
    }

    private static void Enqueue(string message, string source)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            // 포화 — 오래된 항목 드랍 (실시간성 우선)
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

    /// <summary>WriteLoopAsync 전용 — 연결 중일 때만 소비된다 (미연결 로그는 버퍼링).</summary>
    public static bool TryDequeue(out ControlMessage? msg)
    {
        if (!_logs.TryDequeue(out msg)) return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }
}
