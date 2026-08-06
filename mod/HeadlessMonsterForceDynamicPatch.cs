using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>데디서버 몬스터 시뮬레이션 범위 — 그리드 기반 근접 활성화 (2026-08-06, C안).
/// 배경: 무조건 전 세계 Dynamic(ForceDynamic 초기안)은 몬스터의 블록 데미지(버로우/충돌)가
/// 서버·클라의 blockDamages 리스트를 128 한도로 상시 초과시켜, 가장 오래된 항목(장시간 채굴 중인
/// ilmenite 4000HP 등)이 축출되며 누적이 리셋되는 문제를 유발했다 (채굴 불가/체력 롤백).
/// 원인: MP 모드의 BuildingEntity_Update_MultiplayerPatch.NewBuildingOptimizeThing가 바닐라의
/// 청크 렌더러 체크를 CHUNKS_VISIBLE_TO_PLAYERS 그리드(주차된 서버 카메라 + 플레이어 링)로
/// 대체하여 그리드 밖 몬스터를 rb=Static으로 강등한다 → SpiderHandler.Update:138 조기리턴 → 동결.
/// 수정: 그리드 내(플레이어 근처) 몬스터만 Dynamic으로 강제 — 원거리 몬스터는 MP 원본 로직(Static)
/// 유지 (베이스 모드와 동일 동작, 블록 데미지 홈수·CPU 부담 원복). 그리드 밖이어도 렌더러 게이트
/// (SpiderHandler.Update:112 등)는 HeadlessChunkVisibilityPatch가 계속 해제하므로, 플레이어가
/// 접근해 그리드에 들어오는 순간 몬스터가 정상적으로 깨어난다.
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
        if (WorldGeneration.world == null || !WorldGeneration.world.worldExists) return true;
        if (WorldGeneration.world.generatingWorld) return true;

        GameObject go = rb.gameObject;
        if (go.GetComponent<SpiderHandler>() == null && go.GetComponent<GrabberPlant>() == null)
        {
            BuildingEntity bld = go.GetComponent<BuildingEntity>();
            if (bld == null || !bld.animal) return true;
        }

        // 그리드 내(플레이어/카메라 링) — Dynamic 강제, MP 원본 스킵.
        // 그리드 밖 — 원본(NewBuildingOptimizeThing → Static) 실행 허용.
        if (SharedMain.CheckIfChunkOnThisPositionIsVisibleByAnyPlayer(rb.position))
        {
            rb.bodyType = RigidbodyType2D.Dynamic;
            return false;
        }
        return true;
    }
}
