using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 서버측 geyser 발동 수정 ( 프로덕션 실측 기반).
// 문제: 바닐라 GeyserScript.OnTriggerEnter2D의 발동 게이트가
// `PlayerCamera.main.transform.position`(서버 = 주차된 호스트 (482,490)) 기준 64 유닛
// 이내라서, 플레이어 근처 geyser는 서버에서 발동하지 않는다 -> 서버 fluid 배열에 유체가
// 생성되지 않아 클라 로컬 유체가 서버 동기화(10154 - 빈 배열)로 증발하고
// 서버측 음수 경로(FluidManager.GetLiquid - 빈 배열)가 실패해 갈증이 회복되지 않는다.
// 수정: 서버에서만 카메라 거리 게이트 대신 "살아있는 플레이어 64 유닛 이내"로 발동 판정.
// 클라(원본 게이트 - 자기 카메라 = 자기 몸)는 무관하게 유지.
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
        return false; // 원본(주차 카메라 거리 게이트) 스킵
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
