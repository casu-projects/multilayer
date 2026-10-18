using HarmonyLib;
using Together;

namespace CasuMod;

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
internal static class PreRunScript_StartRun_RuleBootstrapPatch
{
    private static void Postfix(PreRunScript __instance)
    {
        if (__instance?.runSettings == null)
            return;

        int applied = RuleApplier.ApplyRulesNow();

        Plugin.Log.LogInfo($"[Rule] KrokoshaMP 규칙 적용 {applied}건 (내장 세이브: SavePlayerState={Multiplayer.rules.SavePlayerState}, SavePlayerInventory={Multiplayer.rules.SavePlayerInventory}, SavePlayerPosition={Multiplayer.rules.SavePlayerPosition}).");
    }
}
