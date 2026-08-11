using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 오케스트레이터 전달 MP 규칙(rule.json)을 StartRun 직후 적용 (RuleApplier 공용 로직).
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
