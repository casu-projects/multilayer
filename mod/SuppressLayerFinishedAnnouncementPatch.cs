using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;

namespace CasuMod;

// "X finished the layer" 채팅 알림 제거 — finishedLayer를 미리 설정해 원본 감지 루프를 무력화.
[HarmonyPatch(typeof(ServerMain), "UpdateSyncClientsToClients")]
internal static class SuppressLayerFinishedAnnouncementPatch
{
    private static void Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || !Util.IsInWorld())
        {
            return;
        }

        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists || world.generatingWorld)
        {
            return;
        }

        if (Traverse.Create(world).Field("doingRegen").GetValue<bool>())
        {
            return;
        }

        foreach (KeyValuePair<Body, NetPlayer> item in NetPlayer.BodyToPlayerDict)
        {
            if (item.Key.alive && item.Key.conscious && item.Value.IsAtTheEndOfLayer() && !item.Value.finishedLayer)
            {
                item.Value.finishedLayer = true;
            }
        }
    }
}
