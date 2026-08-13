using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 서버측 geyser 발동 수정 ( 프로덕션 실측 기반)
// 문제: 바닐라 GeyserScript.OnTriggerEnter2D의 발동 게이트가
// `PlayerCamera.main.transform.position`(서버 = 주차된 호스트 (482,490)) 기준 64 유닛
// 이내라서, 플레이어 근처 geyser는 서버에서 발동하지 않는다 -> 서버 fluid 배열에 유체가
// 생성되지 않아 클라 로컬 유체가 서버 동기화(10154 - 빈 배열)로 증발하고
// 서버측 음수 경로(FluidManager.GetLiquid - 빈 배열)가 실패해 갈증이 회복되지 않는다
// 수정: 서버에서만 카메라 거리 게이트 대신 "살아있는 플레이어 64 유닛 이내"로 발동 판정
// 클라(원본 게이트 - 자기 카메라 = 자기 몸)는 무관하게 유지
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

// 서버 게이저 유체 커버 - 활성화 창(4.5초) 동안 같은 열(y+0..9)의 **빈 타일만** 게이저 타입으로
// 채운다. 클라이언트는 순정 모드로 자기 Random 위치에 로컬 펌프하므로, 서버가 열의 빈 타일을
// 미리 채워 두면 클라 펌프 위치가 항상 서버 싱크 상태에 포함되어 싱크가 클라 유체를 지우지
// 않는다. 기존 fluid는 덮어쓰지 않는다 - 시뮬 결과(녹조류 변환 등)를 되돌리지 않게.
[HarmonyPatch(typeof(GeyserScript), "Update")]
internal static class GeyserServerFillEmptyTilesPatch
{
    private static readonly FieldInfo ActivateTimeField = AccessTools.Field(typeof(GeyserScript), "activateTime");
    private static readonly FieldInfo LiquidTypeField = AccessTools.Field(typeof(GeyserScript), "liquidType");

    private static void Postfix(GeyserScript __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null) return;
        if (Time.time - (float)ActivateTimeField.GetValue(__instance) >= 4.5f) return;

        byte liquidType = (byte)LiquidTypeField.GetValue(__instance);
        Vector2Int pos = WorldGeneration.world.WorldToBlockPos(__instance.transform.position);
        for (int i = 0; i < 10; i++)
        {
            Vector2Int cell = new Vector2Int(pos.x, pos.y + i);
            if (FluidManager.main.fluid[cell.x, cell.y] == 0)
            {
                FluidManager.main.fluid[cell.x, cell.y] = liquidType;
            }
        }
    }
}
