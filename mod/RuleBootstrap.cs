using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>오케스트레이터 전달 MP 규칙(rule.json) 적용 — PreRunScript.StartRun 직후 (Postfix).
/// 월드젠 완료 전이므로 시작 보급품/퇴장 저장 등 모든 규칙 소비 시점에 앞선다.
/// 실제 반영 로직은 RuleApplier.ApplyRulesNow 공용 (RUN_RULES_STATE 실시간 푸시와 동일).</summary>
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_RuleBootstrapPatch
{
    private static void Postfix(PreRunScript __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance?.runSettings == null)
            return;

        int applied = RuleApplier.ApplyRulesNow();

        Plugin.Log.LogInfo($"[Rule] KrokoshaMP 규칙 적용 {applied}건 (내장 세이브: SavePlayerState={KrokoshaScavMultiplayer.rules.SavePlayerState}, SavePlayerInventory={KrokoshaScavMultiplayer.rules.SavePlayerInventory}, SavePlayerPosition={KrokoshaScavMultiplayer.rules.SavePlayerPosition}).");
    }
}
