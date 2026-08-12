using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 도착/동결(마이그레이션) 플레이어의 forcedelete 큐에서 **본인 netId**를 제거 .
// 문제: 목적지에 도착한 플레이어가 본인 CharSync 객체(netId==clientId)의 정보를 요청/ACK하면
// (Client_RequestObjectInfo -> Server_ResetDeltaOnObj, 또는 ACK 스냅샷 처리 -> Server_ReceiveAck),
// 목적지의 per-plr objstate가 아직 생성되지 않아 forcedelete_queue에 본인 netId가 적재된다.
// 이 forcedelete(10174 result4==0)가 클라에 전송되면 CharSync.Client_DeleteObject -> DestroyNPC로
// **로컬 바디가 파괴**되어 NRE 폭풍 + NO LOCAL NETBODY + 관전 모드가 된다 (와이어탭 실측:
// 목적지 fd=[2] 큐잉 직후 클라 바디 소멸).
// 해결: 적재 직후(Postfix) 마이그레이션 창(동결/도착 차단/감시) 동안 본인 netId를 큐에서 제거.
// 본인 바디는 연결 중 삭제될 일이 없으므로 억제가 안전하다. 다른 객체의 forcedelete는 유지.
[HarmonyPatch]
internal static class ForcedeleteOwnNetIdSuppressPatch
{
    private static readonly FieldInfo StatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    private static bool IsMigrating(NetPlayer plr)
    {
        if (plr == null) return false;
        string pid = plr.GetPersistentId();
        return MigrationModule.IsFrozen(pid) || MigrationModule.IsArrivalBlocked(plr);
    }

    private static void PurgeOwnForcedelete(NetPlayer plr)
    {
        try
        {
            foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
            {
                if (sys.syncsystemid != 2) continue; // CharSync만 - PlrSync fd는 무해(베이스 Client_DeleteObject no-op)
                if (StatesField?.GetValue(sys) is not IDictionary states) continue;
                if (!states.Contains(plr.clientId)) continue;
                object state = states[plr.clientId];
                var queue = state.GetType().GetField("forcedelete_queue")?.GetValue(state) as Queue<knetid>;
                if (queue == null || !queue.Contains(plr.clientId)) continue;

                var rebuilt = new Queue<knetid>(queue.Where(id => (ushort)id != (ushort)plr.clientId));
                state.GetType().GetField("forcedelete_queue")?.SetValue(state, rebuilt);
            }
        }
        catch (Exception)
        {
        }
    }

 // Server_ResetDeltaOnObj - 클라이언트의 객체 정보 요청 처리 (본인 netId 요청이
 // objstate 부재로 forcedelete가 되는 경로).
    [HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Server_ResetDeltaOnObj")]
    internal static class ResetDeltaPatch
    {
        private static void Postfix(knetid plrId, knetid objId)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if ((ushort)plrId != (ushort)objId) return;   // 본인 netId 요청만
            if (!NetPlayer.TryGetPlayerFromClientId(plrId, out NetPlayer plr)) return;
            if (IsMigrating(plr)) PurgeOwnForcedelete(plr);
        }
    }

 // Server_ReceiveAck - ACK 스냅샷 처리에서 서버 객체가 없으면 forcedelete가 되는 경로.
    [HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Server_ReceiveAck")]
    internal static class ReceiveAckPatch
    {
        private static void Postfix(NetPlayer plr)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server || plr == null) return;
            if (IsMigrating(plr)) PurgeOwnForcedelete(plr);
        }
    }
}
