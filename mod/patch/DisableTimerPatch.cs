using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class DisableTimerPatch
{
    private static void Prefix(WorldGeneration __instance)
    {
        if (__instance == null) return;
        if (__instance.layerTimeSpent != 0f) __instance.layerTimeSpent = 0f;
    }
}
