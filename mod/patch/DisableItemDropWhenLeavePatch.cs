using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.DestroyCharacterIfNoLocal))]
internal static class DisableItemDropWhenLeavePatch
{
    private static bool Prefix(NetPlayer __instance)
    {
        // 로컬 placeholder 바디(PlayerCamera.main.body)는 유지한다
        if (__instance == null || __instance.is_local)
            return false;

        if (__instance.body != null)
        {
            if ((object)__instance.body == (object)InvButton_get_body_MultiplayerPatch.focused_body)
            {
                InvButton_get_body_MultiplayerPatch.focused_body = null;
            }

            NetPlayer.BodyToPlayerDict.Remove(__instance.body);
            NetBody.DestroyNPC(__instance.body);
            __instance.body = null;
        }

        return false;
    }
}
