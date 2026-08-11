using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>객체 시스템 fastsync 수집 주기 적응 조정 (2026-08-11, CPU 최적화).
/// Server_RunFastSync는 0.04초마다 플레이어당 GatherClosestObjectsToSync(Physics2D
/// OverlapCircleAll 반경 70)를 실행한다 — 7명이면 초당 175회의 물리 쿼리.
/// 플레이어 5명 이상이면 간격을 0.08초로 늘려 쿼리를 절반으로 줄인다. 새 객체는
/// Server_NewObject/등록 경로가 별도로 forcesync를 큐잉하므로 수집 지연은 무해하고,
/// 라운드로빈+스냅샷 마진 패치가 갱신을 보장한다.</summary>
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
