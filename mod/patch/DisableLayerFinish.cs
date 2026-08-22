using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ServerMain), "UpdateSyncClientsToClients")]
internal static class DisableLayerFinishPatch
{
    private static void Prefix()
    {
        if (!Util.IsInWorld())
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
