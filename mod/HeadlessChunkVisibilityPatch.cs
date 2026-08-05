using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CasuMod;

/// <summary>헤드리스 데디서버 몬스터 AI/물리 동결 수정 (2026-08-06, 프로브 실측 기반).
/// 원인: 바닐라의 "body optimize" 게이트가 해당 위치의 청크 렌더러 enabled를 조건으로
/// 사용한다 (SpiderHandler.Update:112, BuildingEntity.Update:81 → rb Static 강등,
/// GrabberPlant.Update:48 등). 데디서버는 카메라가 플레이어와 무관한 위치(482,490 —
/// MP가 호스트를 월드 밖에 주차)에 있어, 월드젠 시점 스폰 주변 24/256 청크만 켜진 채
/// 원격 플레이어 주변 몬스터는 영구 정지한다. 서버(-nographics)는 렌더 비용이 없으므로
/// 모든 청크 렌더러를 매 프레임 강제 활성화해 전 세계 시뮬레이션을 유지한다.</summary>
[HarmonyPatch]
internal static class HeadlessChunkVisibilityPatch
{
    internal static void ForceEnableAllRenderers()
    {
        if (WorldGeneration.world == null || WorldGeneration.world.generatingWorld) return;
        TilemapRenderer[,] renderChunks = WorldGeneration.world.renderChunks;
        if (renderChunks == null) return;
        foreach (TilemapRenderer r in renderChunks)
        {
            if (r != null && !r.enabled) r.enabled = true;
        }
    }
}

/// <summary>매 프레임 강제 활성 — 어떤 코드가 꺼도 다음 프레임에 복구된다.
/// (SharedMain.Update는 서버에서 매 프레임 실행 확인 — 프로브 실측)</summary>
[HarmonyPatch]
internal static class HeadlessChunkVisibility_SharedMainUpdateHook
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SharedMain), "Update");
    }

    private static void Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        HeadlessChunkVisibilityPatch.ForceEnableAllRenderers();
    }
}

/// <summary>바닐라 카메라 링 갱신(UpdateChunkVisibility)은 서버에서 무효화 —
/// 카메라가 플레이어와 동떨어진 곳에 있어 실행되면 플레이어 주변 청크를 꺼버린다.</summary>
[HarmonyPatch]
internal static class HeadlessChunkVisibility_UpdateChunkVisibilityHook
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(WorldGeneration), "UpdateChunkVisibility");
    }

    private static bool Prefix()
    {
        if (KrokoshaScavMultiplayer.is_dedicated_server)
        {
            HeadlessChunkVisibilityPatch.ForceEnableAllRenderers();
            return false; // 원본 실행 생략 — 카메라 링 로직 무효화
        }
        return true;
    }
}
