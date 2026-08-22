using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod.Patch;

// 바닐라/베이스 모드 안정성 가드 이식 (구 모드 project/mod 이식)
// NetPlayer.GetDistanceToNearestLivingPlayer NRE - 퇴장 후 sync 목록에 잔존하는
// null-body 항목으로 인한 폭풍을 차단 (퇴장 직후 NRE 폭풍의 원인)
// AllLivingPlayers에 Unity 지연 Destroy~OnDestroy 사이 잔존하는 null-body 항목이
// 있을 때 폭발하는 NRE를 차단한다. 메서드를 대체해 null/destroyed 항목을 건너뛴다
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.GetDistanceToNearestLivingPlayer))]
internal static class NetPlayer_GetDistanceToNearestLivingPlayer_NullGuardPatch
{
    private static bool Prefix(Vector2 target, ref (NetPlayer, float) __result)
    {
        float best = float.PositiveInfinity;
        NetPlayer nearest = null;

        foreach (NetPlayer candidate in NetPlayer.AllLivingPlayers)
        {
            // 퇴장 처리 중이거나 몸체가 이미 제거된 플레이어는 거리 계산에서 제외한다
            if (candidate == null || candidate.body == null)
            {
                continue;
            }

            float distSqr = KM.dist2dsqr((Vector2)candidate.body.transform.position, in target);
            if (distSqr < best)
            {
                best = distSqr;
                nearest = candidate;
            }
        }

        __result = (nearest, best);
        return false;
    }
}
