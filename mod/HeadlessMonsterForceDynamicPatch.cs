using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>데디서버 몬스터 AI/물리 동결 수정 2 (2026-08-06, 코드 분석 확정).
/// 원인: MP 모드의 BuildingEntity_Update_MultiplayerPatch.Postfix → NewBuildingOptimizeThing가
/// 바닐라의 청크 렌더러 enabled 체크를 CHUNKS_VISIBLE_TO_PLAYERS 그리드(주차된 서버 카메라(482,490)
/// 링 + 플레이어 링만 참)로 대체하여, 그리드 밖 몬스터를 rb=Static으로 강등한다.
/// → SpiderHandler.Update:138 조기리턴 → 영구 동결 (프로브의 chunk=True/enabled=256과 무관 — MP가
/// 렌더러를 보지 않으므로 HeadlessChunkVisibilityPatch로 해결 불가).
/// 서버(-nographics)는 렌더링이 없으므로 몬스터는 항상 Dynamic이어야 전 세계 시뮬레이션이 돈다.
/// 스코프: SpiderHandler/GrabberPlant 보유 개체 + animal BuildingEntity만 — 일반 건물은 MP 로직 유지.</summary>
[HarmonyPatch]
internal static class HeadlessMonsterForceDynamicPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(BuildingEntity_Update_MultiplayerPatch), "NewBuildingOptimizeThing");
    }

    private static bool Prefix(Rigidbody2D rb)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        if (rb == null) return true;
        if (WorldGeneration.world != null && WorldGeneration.world.generatingWorld) return true;

        GameObject go = rb.gameObject;
        if (go.GetComponent<SpiderHandler>() == null && go.GetComponent<GrabberPlant>() == null)
        {
            BuildingEntity bld = go.GetComponent<BuildingEntity>();
            if (bld == null || !bld.animal) return true;
        }
        rb.bodyType = RigidbodyType2D.Dynamic;
        return false;
    }
}
