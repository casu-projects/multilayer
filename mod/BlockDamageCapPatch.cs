using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CasuMod;

// blockDamages 리스트 상한 상향 (C안) - 128 -> 256.
// 배경: 바닐라 DamageBlock은 리스트가 128개를 넘으면 가장 오래된 항목을 파괴 처리 없이
// 제거한다(WorldGeneration.cs:799-804) - 장시간 누적이 필요한 채굴(ilmenite 4000HP)의
// 데미지가 축출로 리셋되어 파괴가 불가능해진다. 근접 활성화 전환(HeadlessMonsterForceDynamicPatch)
// 으로 홍수가 줄지만, 동시 다발(다수 몬스터·여러 플레이어 채굴) 상황의 안전망으로 상한을 256으로
// 상향한다. 메모리 영향 무시 가능(항목 ≈ pos+damage+sprite 참조). 서버/클라 공통 적용(무해).
// 트랜스파일러: `blockDamages.Count &gt; 128`의 Ldc_I4 128 -> 256 (메서드 내 유일 상수).
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
