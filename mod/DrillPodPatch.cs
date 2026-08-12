using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 드릴 포드 비활성화 (구 모드 DrillPodPatch.cs 이식 + 현행 바닐라 대응).
// 멀티 인스턴스 레이어 시스템에서는 드릴 포드 하강(자체 월드 재생성)이 의미 없고 오히려
// 인스턴스 불변식을 깨뜨린다.
// OnUse 무효화 - 바닐라 OnUse는 수리 키트 상호작용 전용 (구 패치와 동일 - 드릴 완전 무력화).
// OverrideComponent.Update 무효화 - 현행 바닐라(4.0.1)의 실질 하강 경로: 5초 홀드 시
// WorldGeneration.world.doPod = true + RegenerateWorld (DrillPod_Update_MultiplayerPatch.cs).
// 이 경로를 막지 않으면 OnUse만으로는 하강이 그대로 작동한다.
[HarmonyPatch(typeof(DrillPod), "OnUse")]
[HarmonyPriority(Priority.First)]
internal static class DrillPod_OnUse_NoOpPatch
{
    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || !KrokoshaScavMultiplayer.is_server)
            return true;
        return false;
    }
}

// 현행 바닐라 하강 경로 차단 - 전용 서버에서 OverrideComponent.Update를 무효화한다
// (홀드 시간 누적, didTeleport, RegenerateWorld 전부 미실행).
[HarmonyPatch(typeof(DrillPod_Update_MultiplayerPatch.Krokosha_DrillPod_OverrideComponent), "Update")]
[HarmonyPriority(Priority.First)]
internal static class DrillPod_Update_NoOpPatch
{
    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || !KrokoshaScavMultiplayer.is_server)
            return true;
        return false;
    }
}
