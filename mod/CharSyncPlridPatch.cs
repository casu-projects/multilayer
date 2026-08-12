using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// CharSync plrid 상시 전송 (근본 수정).
// 문제: 서버가 보내는 CharSync(10174) 패킷의 plrid(플레이어 ID)는 수신자별 델타
// 인코딩으로 "마지막 전송본과 다를 때만" 실린다 (NetBodySyncPacket.Write
// flag = old.plrid != plrid). 수신자 베이스가 한번 plrid=2로 전진하면 다시 전송될
// 일이 없다. 따라서 (a) 첫 생성 패킷이 CoolSync(비신뢰)에서 유실되거나, (b) 바디
// 항목이 삭제(월드젠 정리/로스터 삭제/마이그레이션)된 후 재생성될 때 - plrid 없는
// 델타로 객체를 만들어 클라이언트가 plrid=0을 읽고, TryGetPlayerFromClientId(0)가
// 실패해 "NPC 바디(신원 미인지)"가 생성된다. NPC 바디는 BodyToPlayerDict에 등록되지
// 않아 (수리 경로도 netBody.plr만 갱신) Shift 방향 표시에 잡히지 않는 영구 스턱이 된다.
// 수정: 플레이어 바디의 수신자별 델타 베이스 plrid를 매 프레임 0으로 리셋 -> 델타가
// 매번 변화로 감지 -> plrid가 모든 CharSync 패킷에 상시 포함된다 (바디당 ~3B/프레임/
// 수신자 - 무시 가능한 비용). 신원을 잃을 수 없으므로 생성/재생성/유실 어느 경우든
// 클라이언트는 항상 플레이어 바디로 생성하고, 이미 NPC화된 바디도 수리 경로로 복구된다.
[HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "PackData1")]
internal static class CharSync_PlridAlwaysSyncPatch
{
    private static FieldInfo _realObjField;
    private static FieldInfo _netIdField;

    private static void Prefix(CoolSyncSubSystemForObjects.Server_PerPlrState plrstate, object obj)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || plrstate == null || obj == null)
            return;

        _realObjField ??= AccessTools.Field(obj.GetType(), "real_obj");
        object realObj = _realObjField?.GetValue(obj);
        if (!(realObj is NetBody nb) || nb.plr == null || nb.plr.is_local)
            return;

        _netIdField ??= AccessTools.Field(obj.GetType(), "netId");
        if (_netIdField?.GetValue(obj) is not knetid netId)
            return;

 // 수신자별 델타 베이스의 plrid를 0으로 되돌린다 - 다음 Write가 변화로 감지해
 // plrid를 항상 패킷에 실는다. (박스된 구조체 복사 후 재할당 - 필드 직접 변경은
 // 박스 본체에 반영되지 않는다.)
        var objState = plrstate.GetObjState(netId);
        if (objState.last_known_snapshot is NetBodySyncPacket basePkt)
        {
            if (basePkt.plrid != 0)
            {
                basePkt.plrid = 0;
                objState.last_known_snapshot = basePkt;
            }
        }
    }
}
