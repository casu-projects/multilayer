using System;
using System.Collections.Generic;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// !group — 그룹 생성/가입/퇴장/삭제/목록/멤버/초대/joinable 토글.
// 전부 오케스트레이터로 요청(GROUP_REQUEST)하고 결과는 개인 회신(GROUP_RESULT)으로 받는다.
// invite는 대상 유저에게만 띄우는 단일 유저 투표 UI를 사용한다.
internal static class GroupCommands
{
    private const string CommandName = "group";

    internal static bool TryHandle(NetPlayer caller, string[] argv)
    {
        if (caller == null || argv.Length == 0 || argv[0] != CommandName) return false;
        if (argv.Length < 2)
        {
            Usage(caller);
            return true;
        }

        switch (argv[1])
        {
            case "create":
                if (argv.Length < 3) { Usage(caller, "create <그룹이름>"); return true; }
                SendRequest(caller, "create", argv[2]);
                return true;
            case "join":
                if (argv.Length < 3) { Usage(caller, "join <그룹이름>"); return true; }
                SendRequest(caller, "join", argv[2]);
                return true;
            case "leave":
                SendRequest(caller, "leave", "");
                return true;
            case "remove":
                SendRequest(caller, "remove", "");
                return true;
            case "list":
                SendRequest(caller, "list", "");
                return true;
            case "players":
                SendRequest(caller, "players", "");
                return true;
            case "joinable":
                SendRequest(caller, "joinable", "");
                return true;
            case "invite":
                // 진행 중인 투표가 있으면 거부 (runvote/banvote와 동일 게이트).
                if (VoteSystem.Server_ActiveVote != null)
                {
                    ChatCommands.ChatPrivateReply.SendToPlayer(caller, "이미 진행 중인 투표가 있습니다. 잠시 후 다시 시도해주세요.");
                    return true;
                }
                if (argv.Length < 3) { Usage(caller, "invite <플레이어이름>"); return true; }
                SendRequest(caller, "invite", "", argv[2]);
                return true;
            default:
                Usage(caller);
                return true;
        }
    }

    private static void SendRequest(NetPlayer caller, string action, string name, string target = "")
    {
        OrchestratorClient.Instance?.SendEvent("GROUP_REQUEST",
            new { playerKey = caller.GetPersistentId(), action, name, target });
    }

    private static void Usage(NetPlayer caller, string? sub = null)
    {
        if (sub != null)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, $"사용법: !{CommandName} {sub}");
            return;
        }
        ChatCommands.ChatPrivateReply.SendToPlayer(caller, "사용법: !group [create <이름>|join <이름>|leave|remove|list|players|invite <이름>|joinable]");
    }

    // GROUP_RESULT — 개인 회신 라인 표시.
    internal static void HandleResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<GroupResultPayload>();
        if (payload == null || payload.Lines == null) return;
        NetPlayer plr = ChatCommands.FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        foreach (string line in payload.Lines)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(plr, line);
        }
    }

    // GROUP_INVITE — 대상 유저에게만 투표 UI (voters 타겟 지정).
    internal static void HandleInvite(ControlMessage msg)
    {
        var payload = msg.PayloadAs<GroupInvitePayload>();
        if (payload == null || string.IsNullOrEmpty(payload.PlayerKey)) return;
        NetPlayer target = ChatCommands.FindByPersistentId(payload.PlayerKey);
        if (target == null) return;

        if (VoteSystem.Server_ActiveVote != null)
        {
            ReportInviteResult(payload, accepted: false, reason: "busy");
            return;
        }
        try
        {
            VoteSystem.Server_AnnounceVote("그룹 초대",
                $"{payload.GroupName}",
                30f,
                (yes, no, ignore) =>
                {
                    // yes/no/ignore — 수락/거절/타임아웃 구분 (오케스트레이터의 거절 통지용).
                    if ((yes?.Count ?? 0) > 0) ReportInviteResult(payload, accepted: true, reason: "accepted");
                    else if ((no?.Count ?? 0) > 0) ReportInviteResult(payload, accepted: false, reason: "declined");
                    else ReportInviteResult(payload, accepted: false, reason: "timeout");
                },
                new List<NetPlayer> { target });
        }
        catch (Exception)
        {
            ReportInviteResult(payload, accepted: false, reason: "busy");
        }
    }

    private static void ReportInviteResult(GroupInvitePayload payload, bool accepted, string reason)
    {
        OrchestratorClient.Instance?.SendEvent("GROUP_INVITE_RESULT",
            new { playerKey = payload.PlayerKey, groupName = payload.GroupName, accepted, reason });
    }
}
