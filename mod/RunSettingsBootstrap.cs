using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>오케스트레이터 전달 런 설정(run.json) 적용 — PreRunScript.StartRun 직전 (Prefix).
/// 월드젠 파라미터로 소비되므로 StartRun 본문 이전에 반영해야 한다.</summary>
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_RunSettingsBootstrapPatch
{
    private static void Prefix(PreRunScript __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance == null || __instance.runSettings == null)
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

        // PreRunScript 인스턴스는 씬 로드로 파괴되므로 WorldGeneration(정적)에 동기화.
        WorldGeneration.runSettings = __instance.runSettings;

        Plugin.Log.LogInfo($"[Run] runSettings 적용 {applied}건.");
    }
}
