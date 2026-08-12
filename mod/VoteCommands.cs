using System;
using System.Collections.Generic;
using System.Globalization;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>채팅 투표 명령 !runvote / !banvote (구 시스템 ChatOnlyVoteCommands 이식 — 2026-08-03).
/// 로컬 검증 후 VOTE_START를 오케스트레이터로 발신 — VoteCoordinator가 전 인스턴스에
/// 투표를 릴레이하고 tally를 합산한다 (바닐라 VoteSystem.Server_AnnounceVote UI 재사용).
/// 두 명령은 ChatCommandRegistry에 개별 등록된다.</summary>
internal static class VoteCommands
{
    private const float VoteTimeSeconds = 30f;

    internal static void CmdRunVote(NetPlayer caller, string[] argv)
    {
        if (VoteSystem.Server_ActiveVote != null)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "이미 진행 중인 투표가 있습니다. 잠시 후 다시 시도해주세요.");
            return;
        }

        if (argv.Length < 3)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "사용법: !runvote <설정이름> <값>.");
            return;
        }

        string key = argv[1];
        string rawValue = argv[2];

        if (string.Equals(key, "debugworld", StringComparison.OrdinalIgnoreCase))
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "debugworld 설정은 투표로 변경할 수 없습니다.");
            return;
        }

        Dictionary<string, object> runSettings = WorldGeneration.runSettings
            ?? PreRunScript.instance?.runSettings;

        if (runSettings == null)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "아직 Run 설정을 사용할 수 없는 상태입니다.");
            return;
        }

        if (!runSettings.TryGetValue(key, out object existing))
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, $"알 수 없는 Run 설정입니다: {key}");
            return;
        }

        object parsedValue;
        try
        {
            string normalized = rawValue;
            if (existing is bool)
            {
                if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase))
                    normalized = "True";
                else if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase))
                    normalized = "False";
            }
            parsedValue = Convert.ChangeType(normalized, existing.GetType(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, $"\"{rawValue}\" 값을 파싱할 수 없습니다: {ex.Message}");
            return;
        }

        string promptBody = $"{key}\n{existing} > {parsedValue}";

        // callerClientId — 오케스트레이터가 투표 거부 시 개인 회신용.
        var payload = new Dictionary<string, string>
        {
            ["key"] = key,
            ["rawValue"] = rawValue,
            ["callerClientId"] = ((ushort)caller.clientId).ToString(CultureInfo.InvariantCulture),
        };
        VoteRelay.EmitVoteStart(Guid.NewGuid().ToString(), "run", "세팅 변경 투표",
            promptBody, VoteTimeSeconds, payload);
    }

    internal static void CmdBanVote(NetPlayer caller, string[] argv)
    {
        if (VoteSystem.Server_ActiveVote != null)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "이미 진행 중인 투표가 있습니다. 잠시 후 다시 시도해주세요.");
            return;
        }

        if (argv.Length < 2)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "사용법: !banvote <플레이어이름>.");
            return;
        }

        // 대상 해석은 오케스트레이터가 수행 (전 레이어 온라인 세션 기준).
        var payload = new Dictionary<string, string>
        {
            ["callerClientId"] = ((ushort)caller.clientId).ToString(CultureInfo.InvariantCulture),
            ["targetQuery"] = argv[1],
        };
        VoteRelay.EmitVoteStart(Guid.NewGuid().ToString(), "ban", "영구 차단 투표",
            promptBody: "", VoteTimeSeconds, payload);
    }
}
