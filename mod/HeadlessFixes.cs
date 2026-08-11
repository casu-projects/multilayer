using HarmonyLib;
using UnityEngine;

namespace CasuMod;

// 헤드리스 환경은 Screen.resolutions가 비어 있어 Settings.DefaultSettings()가 예외를 던지고
// 월드젠이 멈춘다 — 더미 해상도를 채워 방지한다.
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
