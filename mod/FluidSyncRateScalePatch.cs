using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 유체 청크 동기화(10154) 갱신율 스케일링 — 전송 세트가 플레이어×9 청크라 인원이 늘면
// 청크당 갱신 간격이 길어진다. 0.435초 타이머를 `0.435 / max(1, ceil(players/2))`로 조정.
[HarmonyPatch]
internal static class FluidSyncRateScalePatch
{
    private const float BaseInterval = 0.435067f;

    // LateUpdate의 임계값 비교에 사용되는 동적 간격.
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
