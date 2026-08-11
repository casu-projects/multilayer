using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 퇴장 정리의 인벤토리 드랍 방지 — 인벤토리는 OnDestroy Prefix가 이미 제출했으므로
// 드랍하면 재접속 복원 시 바닥과 인벤토리에 중복된다.
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.DestroyCharacterIfNoLocal))]
internal static class NetPlayer_DestroyCharacterIfNoLocal_DedicatedFix
{
    private static bool Prefix(NetPlayer __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
            return true;

        // 로컬 placeholder 바디(PlayerCamera.main.body)는 유지한다.
        if (__instance == null || __instance.is_local)
            return false;

        if (__instance.body != null)
        {
            if ((object)__instance.body == (object)InvButton_get_body_MultiplayerPatch.focused_body)
            {
                InvButton_get_body_MultiplayerPatch.focused_body = null;
            }

            // NOTE: Server_DropAllInventory() 의도적으로 호출하지 않는다.
            NetPlayer.BodyToPlayerDict.Remove(__instance.body);
            NetBody.DestroyNPC(__instance.body);
            __instance.body = null;
        }

        return false;
    }
}
