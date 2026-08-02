using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>오케스트레이터 전달 MP 규칙(rule.json) 적용 — PreRunScript.StartRun 직후 (Postfix).
/// 월드젠 완료 전이므로 시작 보급품/퇴장 저장 등 모든 규칙 소비 시점에 앞선다.</summary>
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_RuleBootstrapPatch
{
    private static void Postfix(PreRunScript __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance?.runSettings == null)
            return;

        var rules = RunRuleState.RulesSnapshot();
        int applied = 0;
        foreach (KeyValuePair<string, string> entry in rules)
        {
            // 런 설정 키와 겹치면 런 설정(run.json) 우선.
            if (__instance.runSettings.ContainsKey(entry.Key))
                continue;

            var field = AccessTools.Field(typeof(KrokoshaMultiplayerGameRules), entry.Key);
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
                var rulesObj = KrokoshaScavMultiplayer.rules;
                field.SetValueDirect(__makeref(rulesObj), converted);
                KrokoshaScavMultiplayer.rules = rulesObj;
                applied++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Rule] 규칙 '{entry.Key}'={entry.Value} 적용 실패 - {ex.Message}");
            }
        }

        Plugin.Log.LogInfo($"[Rule] KrokoshaMP 규칙 적용 {applied}건 (내장 세이브: SavePlayerState={KrokoshaScavMultiplayer.rules.SavePlayerState}, SavePlayerInventory={KrokoshaScavMultiplayer.rules.SavePlayerInventory}, SavePlayerPosition={KrokoshaScavMultiplayer.rules.SavePlayerPosition}).");
    }
}
