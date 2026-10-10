using HarmonyLib;
using Together;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(Multiplayer), nameof(Multiplayer.ValidateClientConnectIntroductionPacket))]
internal static class AllowShortNamesPatch
{
    private static void Postfix(ref bool __result, ref string deny_reason)
    {
        if (!__result && deny_reason == "Name too short.") __result = true;
    }
}
