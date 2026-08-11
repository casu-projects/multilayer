using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// CoolSync 시스템별 패킷 예산 512→1024 — 플레이어당 틱당 동기화 상한을 늘려 다인원
// 롤백(몬스터 순간이동/아이템 사라짐)을 줄인다. 1024는 직접 연결(LiteNetLib) MTU 한계.
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
