using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 객체 시스템 forcesync 큐 캡 100→400/20→80 — 큐가 상시 포화면 새로 드랍된 아이템의
// 큐잉이 스킵된다. Server_RunFastSync의 `(IsAlive() ? 100 : 20)` 상수를 치환.
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
