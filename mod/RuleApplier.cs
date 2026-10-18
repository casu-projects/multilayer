using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Together;

namespace CasuMod;

public static class RuleApplier
{
    public static int ApplyRulesNow()
    {
        var rules = RunRuleState.RulesSnapshot();
        var runSettings = RunRuleState.RunSettingsSnapshot();
        int applied = 0;
        foreach (KeyValuePair<string, string> entry in rules)
        {
            if (runSettings.ContainsKey(entry.Key))
                continue;

            var field = AccessTools.Field(typeof(MultiplayerGameRules), entry.Key);
            if (field == null)
                continue;

            try
            {
                string value = entry.Value;
                if (field.FieldType == typeof(bool))
                {
                    if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
                        value = "True";
                    else if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase))
                        value = "False";
                }

                object converted = Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture);
                var rulesObj = Multiplayer.rules;
                field.SetValueDirect(__makeref(rulesObj), converted);
                Multiplayer.rules = rulesObj;
                applied++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Rule] 규칙 '{entry.Key}'={entry.Value} 적용 실패 - {ex.Message}");
            }
        }
        return applied;
    }

    public static int ApplyRunSettingsNow()
    {
        if (WorldGeneration.runSettings == null)
            return 0;

        var overrides = RunRuleState.RunSettingsSnapshot();
        int applied = 0;
        foreach (KeyValuePair<string, string> entry in overrides)
        {
            if (!WorldGeneration.runSettings.TryGetValue(entry.Key, out object existing))
                continue;

            try
            {
                WorldGeneration.runSettings[entry.Key] =
                    Convert.ChangeType(entry.Value, existing.GetType(), CultureInfo.InvariantCulture);
                applied++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Run] runSettings '{entry.Key}'={entry.Value} 적용 실패 - {ex.Message}");
            }
        }
        return applied;
    }
}
