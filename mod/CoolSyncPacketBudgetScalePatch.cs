using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>CoolSync 시스템별 패킷 예산 확장 (2026-08-11, Phase 1).
/// 기본 MAX_PACKET_SIZE=512는 플레이어당 틱당 객체/바디 전송 상한 — 인원이 늘면
/// 바디당/객체당 실질 동기화율이 급감해 롤백(몬스터 순간이동, 아이템 사라짐)이 발생한다.
/// 전송 계층: Steam 메시지는 512KB까지 무제한, LiteNetLib(직접 연결)는 MTU 1024 한계 —
/// 1024로 상향해 양쪽 모두 호환. 전송량은 틱당 2배지만 델타 압축 덕분에 실제 부담은 제한적.</summary>
[HarmonyPatch(typeof(CoolSyncManager), "OnTransportStart")]
internal static class CoolSyncPacketBudgetScalePatch
{
    private static void Postfix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
        {
            if (sys is CoolSyncSubSystemForObjects objSys)
            {
                objSys.MAX_PACKET_SIZE = 1024;
            }
        }
        Plugin.Log.LogInfo("[Sync] CoolSync 패킷 예산 512 → 1024 적용.");
    }
}
