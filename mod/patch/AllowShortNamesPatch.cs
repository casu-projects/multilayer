using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(KrokoshaScavMultiplayer), nameof(KrokoshaScavMultiplayer.ValidateClientConnectIntroductionPacket))]
internal static class AllowShortNamesPatch
{
    private static void Postfix(ref bool __result, ref string deny_reason)
    {
        if (!__result && deny_reason == "Name too short.") __result = true;
    }
}
