using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

[HarmonyPatch]
internal static class MonsterStuckPatch
{
    private const float MaxCalmSeconds = 30f;

    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SpiderHandler), "Update");
    }

    private static void Prefix(SpiderHandler __instance)
    {
        if (__instance == null) return;
        if (__instance.moveTime > MaxCalmSeconds) __instance.moveTime = MaxCalmSeconds;
    }
}
