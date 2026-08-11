using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>CoolSync 발신 라운드로빈 해제 (2026-08-11, Phase 1).
/// 바닐라 CoolSyncManager.FixedUpdate는 "한 FixedUpdate에 한 시스템만 발신 후 break" —
/// 5개 시스템이 50Hz 슬롯을 나눠 갖고 CharSync(30Hz 설계)가 실질 ~10Hz로 캡된다.
/// 수정: FixedUpdate마다 모든 시스템의 Server_Update를 호출 — 각 시스템이 자기
/// SEND_FREQUENCY(객체 8Hz / CharSync 30Hz / PlrSync 10Hz / 상태 10Hz)대로 자연 발신한다.
/// 서버 발신 총량은 각 시스템의 주파수 게이트에 묶여 있어 폭주하지 않는다.
/// 바디/몬스터 스트림이 3배 빨라져 순간이동·스냅이 크게 줄어든다.</summary>
[HarmonyPatch(typeof(CoolSyncManager), "FixedUpdate")]
internal static class CoolSyncRoundRobinUnlockPatch
{
    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer())
        {
            return false; // 비서버/비활성 — 원본도 내부 가드로 no-op, 여기서도 그대로 생략
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
