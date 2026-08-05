using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>데디서버 몬스터 장기 대기(캄) 시간 상한 (2026-08-06, 프로브 v2 실측).
/// 관측: thornbackelder(프리팹 retreatMoveTime≈400s)가 물린 직후 moveTime≈400으로 설정되어
/// 후퇴 목표 도달 후 약 6.7분간 가만히 서 있음. moveTime&gt;0 동안에는 목표 재굴림이 차단되므로
/// (SpiderHandler.Update:142-146) 플레이어가 인접해도 재추격하지 않음 — "물린 후 추격 풀리고 멈춤" 현상.
/// 서버 전용으로 moveTime을 상한으로 캡해 물림→후퇴→재추격 사이클을 유지시킨다.
/// (플레이어가 때리면 AnimalHit→moveTime=0으로 즉시 재개되는 기존 경로는 유지됨)</summary>
[HarmonyPatch]
internal static class HeadlessMonsterCalmCapPatch
{
    private const float MaxCalmSeconds = 30f;

    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SpiderHandler), "Update");
    }

    private static void Prefix(SpiderHandler __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance.moveTime > MaxCalmSeconds)
        {
            __instance.moveTime = MaxCalmSeconds;
        }
    }
}
