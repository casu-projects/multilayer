using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>유체 청크 동기화(10154) 갱신율 스케일링 (2026-08-11).
/// 문제: WorldChunkSync.FluidTilemapSyncUpdate는 0.435초당 1청크를 라운드로빈 전송한다.
/// 전송 세트 = 전 플레이어 3×3 청크 합집합(플레이어×9) — 7명이면 청크당 ~27초 갱신으로
/// 유체 변경(게이저 분출 등)이 남에게 늦게/안 보이고 되돌아가는 체감을 준다.
/// 수정: LateUpdate의 유체 타이머 임계값 0.435067f를 정적 필드로 치환 —
/// Prefix에서 `0.435 / max(1, ceil(players/2))`로 동적 조정 (7명: ~0.109초 → 청크당 ~7초).
/// 대역폭: 7명 기준 ~9KB/s 추가 — 무시 가능.</summary>
[HarmonyPatch]
internal static class FluidSyncRateScalePatch
{
    private const float BaseInterval = 0.435067f;

    /// <summary>LateUpdate의 임계값 비교에 사용되는 동적 간격 (Prefix에서 갱신).</summary>
    public static float FluidSyncInterval = BaseInterval;

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(WorldChunkSync), "LateUpdate");
    }

    private static void Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        int players = NetPlayer.ClientIdToPlayerDict.Count;
        FluidSyncInterval = players <= 2
            ? BaseInterval
            : BaseInterval / Mathf.Max(1, Mathf.CeilToInt(players * 0.5f));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instr in instructions)
        {
            if (instr.opcode == OpCodes.Ldc_R4 && instr.operand is float v && v == BaseInterval)
            {
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(FluidSyncRateScalePatch), nameof(FluidSyncInterval)));
            }
            else
            {
                yield return instr;
            }
        }
    }
}
