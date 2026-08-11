using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 바닥 아이템 Update 스로틀 — 50유닛 밖 아이템은 0.25초 간격으로 갱신한다. 스킵 중에도
// vanilla가 하던 rb.simulated/affect 게이팅을 재현해 물리 상태가 낡지 않게 한다.
[HarmonyPatch(typeof(Item), "Update")]
internal static class ItemSimThrottlePatch
{
    private const float ThrottleDistSqr = 2500f;  // 50유닛
    private const double Interval = 0.25;

    private static readonly Dictionary<int, double> _nextUpdate = new();
    private static double _lastPruneAt;
    private static int _throttledCount;
    private static double _diagLogAt;

    private static bool Prefix(Item __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        if (__instance == null || __instance.transform == null) return true;
        if (__instance.transform.parent != null) return true;

        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists) return true;

        double now = Time.realtimeSinceStartupAsDouble;

        (NetPlayer, float) nearest = NetPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);
        if (nearest.Item2 <= ThrottleDistSqr) return true;

        int id = __instance.GetInstanceID();
        if (_nextUpdate.TryGetValue(id, out double next) && now < next)
        {
            // 청크 비활성 시 rb.simulated=false — 스로틀로 인한 물리 잔류 방지.
            bool chunkActive = world.worldExists
                && world.GetClosestChunkRenderer(world.WorldToBlockPos(__instance.transform.position))?.enabled == true;
            if (__instance.rb != null)
            {
                __instance.rb.simulated = chunkActive && Time.timeScale <= 5f;
            }
            if (__instance.affect != null)
            {
                __instance.affect.enabled = chunkActive;
            }
            _throttledCount++;
            return false;
        }
        _nextUpdate[id] = now + Interval;

        Prune(now);
        Diag(now);
        return true;
    }

    // 파괴된 아이템의 잔존 엔트리 정리 (60초 미접촉 제거).
    private static void Prune(double now)
    {
        if (now - _lastPruneAt < 10.0) return;
        _lastPruneAt = now;
        List<int> stale = null;
        foreach (KeyValuePair<int, double> kv in _nextUpdate)
        {
            if (kv.Value < now - 60.0)
            {
                (stale ??= new List<int>()).Add(kv.Key);
            }
        }
        if (stale == null) return;
        foreach (int key in stale) _nextUpdate.Remove(key);
    }

    private static void Diag(double now)
    {
        if (!Plugin.VerboseLogging || now < _diagLogAt) return;
        _diagLogAt = now + 60.0;
        Plugin.Log.LogInfo($"[Throttle] 아이템 스로틀 활성 — 60초 스킵 {_throttledCount}회.");
        _throttledCount = 0;
    }
}
