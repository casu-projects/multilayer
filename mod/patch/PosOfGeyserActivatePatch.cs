using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(GeyserScript), "OnTriggerEnter2D")]
internal static class PosOfGeyserActivatePatch
{
    private static bool Prefix(GeyserScript __instance, Collider2D collision)
    {

        if (collision != null && collision.attachedRigidbody != null
            && collision.attachedRigidbody.bodyType == RigidbodyType2D.Dynamic
            && IsPlayerNearby(__instance.transform.position))
        {
            __instance.TryRumble();
        }
        return false; // 원본(주차 카메라 거리 게이트) 스킵
    }

    private static bool IsPlayerNearby(Vector2 pos)
    {
        foreach (ScavPlayer p in ScavPlayer.AllLivingPlayers)
        {
            if (p != null && p.body != null
                && Vector2.Distance(pos, p.body.transform.position) < 64f)
            {
                return true;
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(GeyserScript), "Update")]
internal static class GeyserServerFillEmptyTilesPatch
{
    private static readonly FieldInfo ActivateTimeField = AccessTools.Field(typeof(GeyserScript), "activateTime");
    private static readonly FieldInfo LiquidTypeField = AccessTools.Field(typeof(GeyserScript), "liquidType");

    private static void Postfix(GeyserScript __instance)
    {
        if (__instance == null) return;
        if (Time.time - (float)ActivateTimeField.GetValue(__instance) >= 4.5f) return;

        byte liquidType = (byte)LiquidTypeField.GetValue(__instance);
        Vector2Int pos = WorldGeneration.world.WorldToBlockPos(__instance.transform.position);
        for (int i = 0; i < 10; i++)
        {
            Vector2Int cell = new Vector2Int(pos.x, pos.y + i);
            if (FluidManager.main.fluid[cell.x, cell.y] == 0)
            {
                FluidManager.main.fluid[cell.x, cell.y] = liquidType;
            }
        }
    }
}
