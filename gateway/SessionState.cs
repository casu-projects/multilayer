namespace CasuMpGateway;

// 세션 상태머신 (PLAN.md ).
public enum SessionState
{
 // 어댑터가 세션을 코어에 넘김 - 밴/유지보수/라우팅 조회.
    Accepted,

 // 라우팅 대기 (모르는 유저 - 오케스트레이터의 ROUTE_UPDATE 대기).
    Routing,

 // 백엔드 연결 진행/재시도 중.
    Connecting,

 // 양방향 투명 중계 중.
    Active,

 // 백엔드 교체 중 (클라 패킷 드랍 - ).
    Swapping,

 // 종료 처리 중.
    Closing,

    Closed,
}
