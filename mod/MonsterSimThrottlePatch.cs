using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using UnityEngine;

namespace CasuMod;

// 몬스터 시뮬레이션 스로틀 — 게임 Body.Update는 청크 게이트가 없어 세계의 모든 몬스터
// 바디가 매 프레임 시뮬레이션된다. 플레이어 거리 티어로 갱신 간격을 확대한다.
[HarmonyPatch(typeof(Body), "Update")]
internal static class MonsterSimThrottlePatch
{
    private const float NearDistSqr = 4900f;     // 70유닛 — 매프레임
    private const float MidDistSqr = 25600f;     // 160
    private const float FarDistSqr = 102400f;    // 320
    private const double NearInterval = 0.1;
    private const double MidInterval = 0.3;
    private const double FarInterval = 0.8;

    private static readonly Dictionary<int, double> _nextUpdate = new();
    private static double _lastPruneAt;
    private static int _throttledCount;
    private static double _diagLogAt;

    private static bool Prefix(Body __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        if (__instance == null) return true;
        if (Util.IsBodyLocal(__instance)) return true;
        if (NetPlayer.BodyToPlayerDict.ContainsKey(__instance)) return true;

        double now = Time.realtimeSinceStartupAsDouble;

        (NetPlayer, float) nearest = NetPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);
        double interval;
        float distSqr = nearest.Item2;
        if (distSqr <= NearDistSqr) return true;
        if (distSqr <= MidDistSqr) interval = NearInterval;
        else if (distSqr <= FarDistSqr) interval = MidInterval;
        else interval = FarInterval;

        int id = __instance.GetInstanceID();
        if (_nextUpdate.TryGetValue(id, out double next) && now < next)
        {
            _throttledCount++;
            return false;
        }
        _nextUpdate[id] = now + interval;

        Prune(now);
        Diag(now);
        return true;
    }

    // 파괴된 바디의 잔존 엔트리 정리 — Unity fake-null을 피해 id 기반으로 60초 미접촉 제거.
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
        Plugin.Log.LogInfo($"[Throttle] 몬스터 스로틀 활성 — 60초 스킵 {_throttledCount}회.");
        _throttledCount = 0;
    }
}
