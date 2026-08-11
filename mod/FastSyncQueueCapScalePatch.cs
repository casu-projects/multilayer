using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>객체 시스템 forcesync 큐 캡 상향 (2026-08-11, Phase 1).
/// 바닐라 NewCoolerObjectPacketWriteReadSystem.Server_RunFastSync는
/// `forcesync_queue.Count >= (IsAlive() ? 100 : 20)`이면 새 객체 큐잉을 스킵 —
/// 다인원에서 큐가 상시 100 근처면 새로 드랍된 아이템/변경이 영영 큐잉되지 않는다.
/// 100 → 400 / 20 → 80 으로 상향 (패킷 예산 1024와 함께 드레인 속도도 2배).</summary>
[HarmonyPatch]
internal static class FastSyncQueueCapScalePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            AccessTools.TypeByName("KrokoshaCasualtiesMP.NewCoolerObjectPacketWriteReadSystem"),
            "Server_RunFastSync");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instr in instructions)
        {
            if (instr.opcode == OpCodes.Ldc_I4 && instr.operand is int v)
            {
                if (v == 100) { instr.operand = 400; yield return instr; continue; }
                if (v == 20) { instr.operand = 80; yield return instr; continue; }
            }
            yield return instr;
        }
    }
}
