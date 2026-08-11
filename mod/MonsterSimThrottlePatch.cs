using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using UnityEngine;

namespace CasuMod;

/// <summary>몬스터 시뮬레이션 스로틀 (2026-08-11, CPU 최적화).
/// 게임 Body.Update에는 청크 게이트가 없다 (UpdateChunkVisibility는 renderer.enabled만
/// 토글 — WorldGeneration.cs:1206) — 데디서버에서 세계의 모든 몬스터 바디가 매 프레임
/// HandleGroundedState(BoxCast ×3)+온도+순환+비주얼+사운드를 실행한다. 이는 인원과 무관한
/// 상수 부담이지만 매우 큰 단일 항목이다.
/// 수정: 플레이어 거리 기준 티어 스로틀 — 가까우면 매프레임, 멀수록 간격 확대.
/// AI(SpiderHandler/GrabberPlant — renderer 게이트)와 물리(force-dynamic 패치)는 무관하게
/// 유지되고, 이벤트 기반 데미지/사망 처리도 스로틀과 무관하다. mod의 Body_Update
/// Prefix/Postfix는 Harmony 특성상 계속 실행되어 안전.</summary>
[HarmonyPatch(typeof(Body), "Update")]
internal static class MonsterSimThrottlePatch
{
    private const float NearDistSqr = 4900f;     // 70유닛 — 매프레임
    private const float MidDistSqr = 25600f;     // 160
    private const float FarDistSqr = 102400f;    // 320
    private const double NearInterval = 0.1;     // ~6Hz
    private const double MidInterval = 0.3;      // ~3Hz
    private const double FarInterval = 0.8;      // ~1.2Hz

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

    /// <summary>파괴된 바디의 잔존 엔트리 정리 (Unity fake-null 회피 — id 기반, 60초 미접촉 제거).</summary>
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
