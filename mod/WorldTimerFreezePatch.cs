using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 레이어 타이머 동결 — 서버의 layerTimeSpent를 매 프레임 0으로 고정한다.
// 클라이언트 타이머 표시 = 로컬 maxTimePerLayer - 서버 동기화(layerTimeSpent)이므로
// (WoundView.LayerTimerDisplay) 서버 동기화만 고정하면 클라이언트 수정 없이
// 모든 클라이언트의 표시가 시작값에서 멈춘다. 방사선 라인 발동 조건
// (WorldGeneration.Update: layerTimeSpent > maxTimePerLayer)도 양쪽 모두 불발되어
// 시간 기반 레이어 종료가 제거된다 (지진/맵 끝 마이그레이션과 무관).
[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class WorldGeneration_Update_TimerFreezePatch
{
    private static void Prefix(WorldGeneration __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null) return;

        if (__instance.layerTimeSpent != 0f)
        {
            __instance.layerTimeSpent = 0f;
        }
    }
}
