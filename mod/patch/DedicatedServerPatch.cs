using HarmonyLib;
using Together;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ServerMain), "HandleDedicatedServerUpdate")]
[HarmonyPriority(Priority.First)]
internal static class ServerMain_HandleDedicatedServerUpdate_DisablePatch
{
    private static bool Prefix()
    {
        return false;
    }
}
