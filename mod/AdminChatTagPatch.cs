using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>어드민 채팅 [*ADMIN*] 태그 (로컬 — 동일 인스턴스 표시).
/// Server_PlayerChatMessageSend가 per-client chattag를 보내고, 클라이언트 Compile이
/// "*태그* 이름"으로 렌더링한다 (Chat.TagName). Prefix가 현재 발신자 어드민 여부를
/// PendingAdmin에 기록하고, Transpiler가 사망 태그 결정 직후에
///   if (PendingAdmin) text = "&lt;color=#ff6b6b&gt;ADMIN&lt;/color&gt;";
/// 를 주입한다. 살아있는 어드민만 — 사망 어드민은 바닐라 사망 태그를 유지한다.</summary>
[HarmonyPatch(typeof(Chat), "Server_PlayerChatMessageSend")]
internal static class AdminChatTagPatch
{
    private const string AdminTag = "<color=#ff6b6b>ADMIN</color>";

    /// <summary>Prefix가 설정 — Transpiler 주입 코드가 같은 스레드에서 읽는다.</summary>
    internal static bool PendingAdmin;

    private static void Prefix(knetid clientId)
    {
        PendingAdmin = NetPlayer.TryGetPlayerFromClientId(clientId, out NetPlayer plr)
            && AdminRegistry.IsAdmin(plr)
            && plr.IsAlive();
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
        var codes = new List<CodeInstruction>(instructions);

        // 사망 태그 대입 "text = Lang.MarkMsgAsLocaleKey("plr_chattag_dead")"의
        // call → stloc(text) 지점을 찾아 그 직후에 어드민 주입 코드를 삽입한다.
        // 매칭 실패 시 주입 없음 (바닐라 그대로 — 안전 폴백).
        int textLocal = -1;
        int insertAt = -1;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Call
                || codes[i].operand is not MethodInfo mi
                || mi.Name != "MarkMsgAsLocaleKey")
            {
                continue;
            }
            for (int j = i + 1; j < codes.Count; j++)
            {
                int local = TryReadLocalIndex(codes[j].opcode, codes[j].operand);
                if (local >= 0)
                {
                    textLocal = local;
                    insertAt = j + 1;
                    break;
                }
            }
            break;
        }

        if (textLocal < 0)
        {
            Plugin.Log.LogWarning("[Admin] Server_PlayerChatMessageSend chattag 주입 지점 매칭 실패 — 어드민 태그 미적용.");
            return instructions;
        }

        var skip = il.DefineLabel();
        var injected = new List<CodeInstruction>
        {
            new(OpCodes.Ldsfld, AccessTools.Field(typeof(AdminChatTagPatch), nameof(PendingAdmin))),
            new(OpCodes.Brfalse, skip),
            new(OpCodes.Ldstr, AdminTag),
            new(OpCodes.Stloc_S, (byte)textLocal),
            new(OpCodes.Nop) { labels = { skip } },
        };
        codes.InsertRange(insertAt, injected);
        return codes;
    }

    private static int TryReadLocalIndex(OpCode op, object? operand)
    {
        if (op == OpCodes.Stloc_0) return 0;
        if (op == OpCodes.Stloc_1) return 1;
        if (op == OpCodes.Stloc_2) return 2;
        if (op == OpCodes.Stloc_3) return 3;
        if (op == OpCodes.Stloc_S && operand is byte b) return b;
        if (op == OpCodes.Stloc && operand is int idx) return idx;
        return -1;
    }
}
