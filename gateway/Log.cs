namespace CasuMpGateway;

// 오케스트레이터 로그 릴레이 — LOG 메시지를 제어 채널로 실시간 전송.
// 연결 전 로그는 _outbound 큐에 버퍼링되어 연결 수립 시 자동 flush된다 (기존 보고 큐).
// Core 미배선(부팅 초기) 시에는 콘솔로 폴백한다.
public static class Log
{
    public static GatewayCore? Core { get; set; }

    // 디버그 로그 표시 여부 — 오케스트레이터 VERBOSE 메시지로 설정된다.
    // false면 디버그급 트레이스(P2P 품질/세션 등)가 출력되지 않는다.
    public static bool Verbose;

    public static void Info(string message)
    {
        if (Core != null)
        {
            Core.SendLog(message);
            return;
        }
        System.Console.WriteLine(message);
    }

    // 디버그급 로그 — verbose=false면 숨김.
    public static void Debug(string message)
    {
        if (Verbose) Info(message);
    }
}
