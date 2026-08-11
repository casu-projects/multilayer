using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 몬스터가 30초 이상 멈춰 있는 상태에 박히는 것을 방지 — 짧은 휴식은 유지한다.
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
