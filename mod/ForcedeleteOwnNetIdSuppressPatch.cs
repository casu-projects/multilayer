using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 마이그레이션 창(동결/도착 차단)의 forcedelete 큐에서 본인 netId 제거 — 본인 CharSync
// 객체의 forcedelete가 클라 로컬 바디를 파괴해 NRE 폭풍/관전 모드가 되는 것을 방지한다.
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
                if (sys.syncsystemid != 2) continue; // CharSync만 — PlrSync fd는 무해
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

    // Server_ResetDeltaOnObj — 클라이언트의 객체 정보 요청 처리.
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

    // Server_ReceiveAck — ACK 스냅샷 처리에서 서버 객체가 없으면 forcedelete가 되는 경로.
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
