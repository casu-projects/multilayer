using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// GetDistanceToNearestLivingPlayer NRE 가드 — 퇴장 직후 잔존하는 null-body 항목을 건너뛴다.
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.GetDistanceToNearestLivingPlayer))]
internal static class NetPlayer_GetDistanceToNearestLivingPlayer_NullGuardPatch
{
    private static bool Prefix(Vector2 target, ref (NetPlayer, float) __result)
    {
        float best = float.PositiveInfinity;
        NetPlayer nearest = null;

        foreach (NetPlayer candidate in NetPlayer.AllLivingPlayers)
        {
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
