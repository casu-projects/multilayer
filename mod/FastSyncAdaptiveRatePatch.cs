using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 객체 시스템 fastsync 수집 주기 적응 조정 — 플레이어 5명 이상이면 0.04s→0.08s로
// GatherClosestObjectsToSync 물리 쿼리를 절반으로 줄인다.
[HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Update")]
internal static class FastSyncAdaptiveRatePatch
{
    private static double _lastAdjustAt;

    private static void Postfix(CoolSyncSubSystemForObjects __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || __instance.syncsystemid != 1) return;

        double now = Time.realtimeSinceStartupAsDouble;
        if (now - _lastAdjustAt < 1.0) return;
        _lastAdjustAt = now;

        float interval = NetPlayer.ClientIdToPlayerDict.Count >= 5 ? 0.08f : 0.04f;
        if (__instance.fastsync_interval != interval)
        {
            __instance.fastsync_interval = interval;
        }
    }
}
