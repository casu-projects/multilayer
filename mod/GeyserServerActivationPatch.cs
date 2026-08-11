using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 서버에서 geyser 발동 게이트를 주차 카메라 거리 대신 "살아있는 플레이어 64유닛 이내"로 대체 —
// 원본 게이트로는 플레이어 근처 geyser가 서버 fluid 배열에 유체를 만들지 못한다.
[HarmonyPatch(typeof(GeyserScript), "OnTriggerEnter2D")]
internal static class GeyserServerActivationPatch
{
    private static bool Prefix(GeyserScript __instance, Collider2D collision)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;

        if (collision != null && collision.attachedRigidbody != null
            && collision.attachedRigidbody.bodyType == RigidbodyType2D.Dynamic
            && IsPlayerNearby(__instance.transform.position))
        {
            __instance.TryRumble();
        }
        return false;
    }

    private static bool IsPlayerNearby(Vector2 pos)
    {
        foreach (NetPlayer p in NetPlayer.AllLivingPlayers)
        {
            if (p != null && p.body != null
                && Vector2.Distance(pos, p.body.transform.position) < 64f)
            {
                return true;
            }
        }
        return false;
    }
}
