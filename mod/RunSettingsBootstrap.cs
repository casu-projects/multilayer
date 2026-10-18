using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Together;

namespace CasuMod;

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_RunSettingsBootstrapPatch
{
    private static void Prefix(PreRunScript __instance)
    {
        if (__instance == null || __instance.runSettings == null)
            return;

        var overrides = RunRuleState.RunSettingsSnapshot();
        int applied = 0;
        foreach (KeyValuePair<string, string> entry in overrides)
        {
            if (!__instance.runSettings.TryGetValue(entry.Key, out object existing))
                continue;

            try
            {
                __instance.runSettings[entry.Key] = Convert.ChangeType(entry.Value, existing.GetType(), CultureInfo.InvariantCulture);
                applied++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Run] runSettings '{entry.Key}'={entry.Value} 적용 실패 - {ex.Message}");
            }
        }

        WorldGeneration.runSettings = __instance.runSettings;

        Plugin.Log.LogInfo($"[Run] runSettings 적용 {applied}건.");
    }
}
