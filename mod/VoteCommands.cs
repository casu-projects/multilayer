using System;
using System.Collections.Generic;
using System.Globalization;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 채팅 투표 명령 !banvote - 로컬 검증 후 VOTE_START를 오케스트레이터로 발신, VoteCoordinator가
// 전 인스턴스에 릴레이하고 tally를 합산한다 (바닐라 투표 UI 재사용).
// Run 설정 변경은 투표 대신 콘솔 run 명령/json 편집으로만 처리한다.
internal static class VoteCommands
{
    private const float VoteTimeSeconds = 30f;

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
