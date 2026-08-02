namespace CasuMpGateway;

/// <summary>세션 상태머신 (PLAN.md G3).</summary>
public enum SessionState
{
    /// <summary>어댑터가 세션을 코어에 넘김 — 밴/유지보수/라우팅 조회.</summary>
    Accepted,

    /// <summary>라우팅 대기 (모르는 유저 — 오케스트레이터의 ROUTE_UPDATE 대기).</summary>
    Routing,

    /// <summary>백엔드 연결 진행/재시도 중.</summary>
    Connecting,

    /// <summary>양방향 투명 중계 중.</summary>
    Active,

    /// <summary>백엔드 교체 중 (클라 패킷 드랍 — G1-6).</summary>
    Swapping,

    /// <summary>종료 처리 중.</summary>
    Closing,

    Closed,
}
