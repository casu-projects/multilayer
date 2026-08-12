using System;
using System.Collections.Generic;
using System.Linq;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 크로스 인스턴스 투표 릴레이 (구 시스템 CrossInstanceVoteRelay/Receive 이식).
// VOTE_START 발신 / VOTE_RUN 수신(바닐라 투표 UI + tally 발신) / VOTE_RESULT 수신(공지 + 효과) /
// VOTE_REJECTED 수신(발신자 개인 회신). 오케스트레이터 VoteCoordinator와 페어.
public static class VoteRelay
{
    internal static void EmitVoteStart(string voteId, string kind, string title, string promptBody,
        float timeoutSeconds, Dictionary<string, string> payload)
    {
        OrchestratorClient.Instance?.SendEvent("VOTE_START", new
        {
            voteId,
            kind,
            title,
            promptBody,
            timeoutSeconds,
            payload,
        });
    }

 // 오케스트레이터 VOTE_RUN 수신 - 바닐라 투표 팝업(10182) 실행, 종료 시 tally 보고.
    internal static void HandleRun(VoteRunPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.VoteId)) return;
        if (VoteSystem.Server_ActiveVote != null) return; // 방어 - 중복 시작 무시

        string voteId = payload.VoteId;
        try
        {
            VoteSystem.Server_AnnounceVote(payload.Title, payload.PromptBody, payload.TimeoutSeconds,
                (yes, no, ignore) =>
                {
                    OrchestratorClient.Instance?.SendEvent("VOTE_TALLY", new
                    {
                        voteId,
                        yes = yes?.Count ?? 0,
                        no = no?.Count ?? 0,
                        ignore = ignore?.Count ?? 0,
                    });
                });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Vote] 투표 UI 시작 실패: {ex.Message}");
        }
    }

 // 오케스트레이터 VOTE_RESULT 수신 - 합산 공지 + 가결 시 효과 공지 (적용은 오케스트레이터).
    internal static void HandleResult(VoteResultPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.VoteId)) return;

        int total = payload.Yes + payload.No + payload.Ignore;
        string verdict;
        if (total == 0)
        {
            verdict = "무효";
        }
        else if ((float)payload.Yes / total > 0.5f)
        {
            verdict = "가결";
        }
        else
        {
            verdict = "부결";
        }

        Chat.Server_ChatAnnouncement($"찬성 {payload.Yes}표, 반대 {payload.No}표, 기권 {payload.Ignore}표");
        Chat.Server_ChatAnnouncement($"투표 결과 {verdict}되었습니다.");

        if (verdict == "가결")
        {
            AnnounceEffect(payload.Kind, payload.Payload ?? new Dictionary<string, string>());
        }
    }

 // 오케스트레이터 VOTE_REJECTED 수신 - 발신자에게 개인 회신.
    internal static void HandleRejected(VoteRejectedPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.CallerClientId)) return;

        if (ushort.TryParse(payload.CallerClientId, out ushort callerClientId)
            && NetPlayer.TryGetPlayerFromClientId(callerClientId, out NetPlayer caller))
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, payload.Reason);
        }
    }

    private static void AnnounceEffect(string kind, Dictionary<string, string> payload)
    {
        switch (kind)
        {
            case "ban":
                if (payload.TryGetValue("targetName", out string targetName))
                {
                    Chat.Server_ChatAnnouncement($"{targetName} 님이 밴 처리되어 서버 접속이 차단되었습니다.");
                }
                break;
        }
    }
}

public sealed class VoteRunPayload
{
    public string VoteId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string PromptBody { get; set; } = "";
    public float TimeoutSeconds { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public sealed class VoteResultPayload
{
    public string VoteId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Yes { get; set; }
    public int No { get; set; }
    public int Ignore { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}

public sealed class VoteRejectedPayload
{
    public string CallerClientId { get; set; } = "";
    public string Reason { get; set; } = "";
}
