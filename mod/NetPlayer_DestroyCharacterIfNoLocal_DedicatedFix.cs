using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 퇴장 시 바닐라 DestroyCharacterIfNoLocal의 인벤토리 드랍 방지 (데디서버 전용).
// 바닐라는 퇴장 정리에서 Server_DropAllInventory를 호출해 인벤토리를 바닥에 드랍하는데,
// 인벤토리는 NetPlayer_OnDestroy Prefix(SubmitPlayer)가 이미 오케스트레이터에 제출한 상태라
// 드랍이 발생하면 재접속 복원 시 같은 아이템이 바닥과 인벤토리에 중복 존재하게 된다.
// 따라서 드랍만 생략하고 나머지 바디 정리는 동일하게 수행한다.
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

 // NOTE: Server_DropAllInventory 의도적으로 호출하지 않는다.
 // 인벤토리는 OnDestroy Prefix가 이미 제출했으며, 드랍하면 재접속 복원 시
 // 바닥에 중복 아이템이 남는다.

            NetPlayer.BodyToPlayerDict.Remove(__instance.body);
            NetBody.DestroyNPC(__instance.body);
            __instance.body = null;
        }

        return false;
    }
}
