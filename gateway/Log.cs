namespace CasuMpGateway;

/// <summary>오케스트레이터 로그 릴레이 — LOG 메시지를 제어 채널로 실시간 전송.
/// 연결 전 로그는 _outbound 큐에 버퍼링되어 연결 수립 시 자동 flush된다 (기존 보고 큐).
/// Core 미배선(부팅 초기) 시에는 콘솔로 폴백한다.</summary>
public static class Log
{
    public static GatewayCore? Core { get; set; }

    public static void Info(string message)
    {
        if (Core != null)
        {
            Core.SendLog(message);
            return;
        }
        System.Console.WriteLine(message);
    }
}
