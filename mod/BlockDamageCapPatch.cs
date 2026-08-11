using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CasuMod;

// blockDamages 리스트 상한 128→256 — 바닐라는 초과 시 오래된 항목을 파괴 처리 없이 제거해
// 장시간 채굴 데미지가 리셋된다. DamageBlock의 `Count > 128` 상수를 치환.
[HarmonyPatch(typeof(WorldGeneration), "DamageBlock",
    new[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
internal static class WorldGeneration_BlockDamageCapPatch
{
    private const int NewBlockDamageCap = 256;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instr in instructions)
        {
            if (instr.opcode == OpCodes.Ldc_I4 && instr.operand is int value && value == 128)
            {
                instr.operand = NewBlockDamageCap;
            }
            yield return instr;
        }
    }
}
