using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>zero-rva 예외 가드 — MP의 Body_Start_MultiplayerPatch.Postfix 전체를 try/catch로
/// 래핑 (2026-08-08, 프로덕션 실측 기반).
/// 실측: 그래픽 모드 전환 후 최초 바디 생성 시 MP의 Postfix가
/// `BadImageFormatException: Method has zero rva`를 던져 (Physics2D.IgnoreLayerCollision /
/// FindObjectsOfType&lt;Body&gt; — DMD 컨텍스트의 네이티브/제네릭 해석 실패) 바디 생성이
/// 중단되고 인스턴스 불안정 → 연결 루프/크래시 처리 홍수로 이어졌다 (ikdasm IL 덤프로
/// 예외 오프셋 0xa3 = 해당 호출 지점 확정).
/// Postfix 내부 예외는 대부분 비필수 보조 로직(충돌 무시 설정 등) — 예외를 삼켜도 바디는
/// 생성되며, 핵심 복원(server_lastplayerstates.Apply)은 Postfix 내부 자체 try/catch로
/// 이미 보호되어 있다. 이 가드로 바디 생성 체인이 어떤 예외에도 죽지 않게 한다.</summary>
[HarmonyPatch]
internal static class BodyStartMpPostfixZeroRvaGuard
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Body_Start_MultiplayerPatch), "Postfix");

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
        var list = new List<CodeInstruction>(instructions);

        // 마지막 ret 제거 (원본 리턴을 try 밖으로 이동)
        if (list.Count > 0 && list[list.Count - 1].opcode == OpCodes.Ret)
        {
            list.RemoveAt(list.Count - 1);
        }

        // try { 원본 바디 } catch (Exception) { /* 삼킴 */ }
        il.BeginExceptionBlock();
        il.BeginCatchBlock(typeof(Exception));
        list.Add(new CodeInstruction(OpCodes.Pop));
        il.EndExceptionBlock();
        list.Add(new CodeInstruction(OpCodes.Ret));

        return list;
    }
}
