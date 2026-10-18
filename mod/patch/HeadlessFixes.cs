using HarmonyLib;
using UnityEngine;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(Screen), nameof(Screen.resolutions), MethodType.Getter)]
internal static class Screen_Resolutions_HeadlessFallbackPatch
{
    private static void Postfix(ref Resolution[] __result)
    {
        if (__result != null && __result.Length > 0)
        {
            return;
        }

        __result = new[]
        {
            new Resolution
            {
                width = 1920,
                height = 1080,
                refreshRateRatio = new RefreshRate { numerator = 60, denominator = 1 },
            },
        };
    }
}
