using HarmonyLib;
using UnityEngine;

namespace CasuMod;

[HarmonyPatch(typeof(WorldGeneration), "FinishWorldGeneration")]
internal static class WorldSnapshot_FinishWorldGenerationPatch
{
    private static void Postfix()
    {
        MapModule.Instance?.OnWorldGenerated();
    }
}
