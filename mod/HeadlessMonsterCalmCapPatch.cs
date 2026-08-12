using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 데디케이트 서버에서 일부 몬스터가 오랫동안 가만히 있는 상태에 박히는 문제를 막아줌.
// 원본 게임처럼 짧은 휴식은 유지하면서, 30초를 넘는 길게 멈춰 있는 현상만 지운다.
[HarmonyPatch]
internal static class HeadlessMonsterCalmCapPatch
{
    private const float MaxCalmSeconds = 30f;

    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SpiderHandler), "Update");
    }

    private static void Prefix(SpiderHandler __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance == null) return;
        if (__instance.moveTime > MaxCalmSeconds)
        {
            __instance.moveTime = MaxCalmSeconds;
        }
    }
}
