using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

[HarmonyPatch]
[HarmonyPriority(Priority.First)]
internal static class DisableDrillPodPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DrillPod), "OnUse");
        yield return AccessTools.Method(typeof(DrillPod_Update_MultiplayerPatch.Krokosha_DrillPod_OverrideComponent), "Update");
    }

    private static bool Prefix() => false;
}