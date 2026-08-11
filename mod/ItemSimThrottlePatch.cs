using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>바닥 아이템 Update 스로틀 (2026-08-11, CPU 최적화).
/// Item.Update는 renderer 게이트로 활성 청크 밖은 스킵되지만, 활성 청크(5×5 그리드 + linger)에
/// 있는 아이템은 전부 매프레임 decay+물리 게이팅을 실행한다 — 베이스에 수백 개가 쌓이면
/// ~0.5-1ms/프레임의 누적 부담.
/// 수정: 최근접 플레이어 50유닛 밖의 바닥 아이템만 0.25초 간격으로 스로틀 (decay/wetness만
/// 느려짐 — 시야 밖이라 무관). 스킵 중에도 vanilla가 하던 rb.simulated/affect.enabled
/// 게이팅을 재현해 물리 상태가 낡지 않게 한다 (청크가 꺼지면 물리도 꺼짐).
/// 컨테이너/착용 아이템(parent != null)은 제외.</summary>
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
            // vanilla Item.Update가 매프레임 하던 rb/affect 게이팅 재현 — 물리 상태 보존.
            // (청크 비활성 시 rb.simulated=false — 스로틀로 인한 물리 잔류 방지)
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

    /// <summary>파괴된 아이템의 잔존 엔트리 정리 (60초 미접촉 제거).</summary>
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
