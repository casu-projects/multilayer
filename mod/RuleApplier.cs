using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 런/규칙 설정 실시간 재적용 - RUN_RULES_STATE 수신(푸시) 시 인스턴스에 즉시 반영.
// RuleBootstrap/RunSettingsBootstrap(StartRun 시점)과 로직을 공유 - 월드 생성 전 수신(no-op)은
// 부트스트랩이, 런타임 수신은 이 클래스가 적용한다 (인바운드 큐는 메인 스레드 - Unity 접근 안전).
public static class RuleApplier
{
 // KrokoshaMultiplayerGameRules(정적 구조체)에 rule.json 전체를 반영.
 // bool 필드는 1/0 -> True/False 문자열 변환 후 Convert.ChangeType (RuleBootstrap 동일 규약).
 // 런 설정(run.json)과 겹치는 키는 런 설정 우선 스킵. 반환: 적용 건수.
    public static int ApplyRulesNow()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
            return 0;

        var rules = RunRuleState.RulesSnapshot();
        var runSettings = RunRuleState.RunSettingsSnapshot();
        int applied = 0;
        foreach (KeyValuePair<string, string> entry in rules)
        {
 // 런 설정 키와 겹치면 런 설정(run.json) 우선.
            if (runSettings.ContainsKey(entry.Key))
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
        return applied;
    }

 // WorldGeneration.runSettings(정적)에 run.json 전체를 머지 - 실시간 값 읽기에 반영.
 // 기존 값 타입 기준으로 Convert.ChangeType (RunSettingsBootstrap 동일 규약).
 // 반환: 적용 건수. 월드 생성 전(정적 null)에는 0.
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
