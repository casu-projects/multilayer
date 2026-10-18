using HarmonyLib;
using Together;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.DestroyCharacterIfNoLocal))]
internal static class DisableItemDropWhenLeavePatch
{
    private static bool Prefix(ScavPlayer __instance)
    {
        if (__instance == null || __instance.IsLocal)
            return false;

        if (__instance.body != null)
        {
            if ((object)__instance.body == (object)InvButton_get_body_MultiplayerPatch.focused_body)
            {
                InvButton_get_body_MultiplayerPatch.focused_body = null;
            }

            ScavPlayer.BodyToPlayerDict.Remove(__instance.body);
            NetBody.DestroyNPC(__instance.body);
            __instance.body = null;
        }

        return false;
    }
}
