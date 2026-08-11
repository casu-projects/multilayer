using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 드릴 포드 비활성화 — 자체 월드 재생성(레이어 전진)이 인스턴스 불변식을 깨뜨린다.
// OnUse와 OverrideComponent.Update(현행 하강 경로)를 모두 무효화한다.
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
