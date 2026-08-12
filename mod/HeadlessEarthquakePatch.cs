using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 헤드리스 서버의 지진 파괴 중심을 안보이는 호스트의 카메라가 아니라 플레이 중인 유저로 바꾼다
[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class HeadlessEarthquakePatch
{
    private const float QuakeDestructionRate = 16f;

    private static void Prefix(WorldGeneration __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || __instance.earthquakeIntensity <= 0f) return;

        foreach (NetPlayer player in NetPlayer.AllLivingPlayers)
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
                // 서버 카메라 주변을 부수는 원본 분기만 끄고, 각 플레이어가 처리를 대신 맡게함
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
