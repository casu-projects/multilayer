using HarmonyLib;
using UnityEngine;

namespace CasuMod.Patch;

// 헤드리스(-batchmode -nographics) 환경에서는 Screen.resolutions가 비어 있어
// 바닐라 Settings.DefaultSettings가 IndexOutOfRangeException을 던지고, 이게 월드젠의
// WorldPreprocess 코루틴을 죽여 세계 생성이 영구 정지한다. 더미 해상도를 채워 방지한다
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
