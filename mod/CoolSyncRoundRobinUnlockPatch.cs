using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// CoolSync 발신 라운드로빈 해제 — 바닐라는 FixedUpdate마다 한 시스템만 발신해 CharSync가
// 30Hz 설계 대신 ~10Hz로 캡된다. 모든 시스템이 자기 SEND_FREQUENCY대로 발신하게 한다.
[HarmonyPatch(typeof(CoolSyncManager), "FixedUpdate")]
internal static class CoolSyncRoundRobinUnlockPatch
{
    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer())
        {
            return false;
        }

        foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
        {
            try
            {
                sys.Server_Update();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Sync] {sys.GetType().Name} Server_Update 실패: {ex.Message}");
            }
        }
        return false;
    }
}
