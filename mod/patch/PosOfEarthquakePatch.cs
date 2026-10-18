using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class PosOfEarthquakePatch
{
    private const float QuakeDestructionRate = 16f;

    private static void Prefix(WorldGeneration __instance)
    {
        if (__instance == null || __instance.earthquakeIntensity <= 0f) return;

        foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
        {
            if (player == null || player.body == null) continue;
            if (UnityEngine.Random.value / Time.deltaTime < QuakeDestructionRate * __instance.earthquakeIntensity)
            {
                __instance.SetBlock(
                    __instance.WorldToBlockPos(
                        (Vector2)player.body.transform.position
                        + UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(5f, 30f)), 0);
            }
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].opcode == OpCodes.Ldc_R4
                && list[i].operand is float value && value == QuakeDestructionRate
                && IsEarthquakeIntensityMultiply(list, i))
            {
                list[i].operand = 0f;
            }
            yield return list[i];
        }
    }

    private static bool IsEarthquakeIntensityMultiply(List<CodeInstruction> list, int index)
    {
        int end = Mathf.Min(list.Count, index + 6);
        for (int i = index + 1; i < end; i++)
        {
            if (list[i].opcode == OpCodes.Ldfld
                && list[i].operand != null
                && list[i].operand.ToString().Contains("earthquakeIntensity"))
            {
                return true;
            }
            if (list[i].opcode == OpCodes.Mul) return false;
        }
        return false;
    }
}
