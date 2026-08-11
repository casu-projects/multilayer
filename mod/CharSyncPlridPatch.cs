using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// CharSync 패킷의 plrid(플레이어 ID) 상시 전송 — 델타 인코딩이 plrid를 유실 시
// 클라이언트가 "NPC 바디(신원 미인지)"를 생성해 영구 스턱이 되는 것을 방지.
// 수신자별 델타 베이스의 plrid를 매번 0으로 되돌려 항상 변화로 감지하게 한다.
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

        // 박스된 구조체는 필드 직접 변경이 본체에 반영되지 않으므로 복사 후 재할당한다.
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
