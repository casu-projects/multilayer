using CasuMp.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using Together;

namespace CasuMod;

public static class VoteRelay
{
    internal static void EmitVoteStart(string voteId, string kind, string title, string promptBody,
        float timeoutSeconds, Dictionary<string, string> payload)
    {
        OrchestratorClient.Instance?.SendEvent(MessageTypes.VoteStart, new
        {
            voteId,
            kind,
            title,
            promptBody,
            timeoutSeconds,
            payload,
        });
    }

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
                    OrchestratorClient.Instance?.SendEvent(MessageTypes.VoteTally, new
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

    internal static void HandleResult(VoteResultPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.VoteId)) return;

        int total = payload.Yes + payload.No + payload.Ignore;
        string verdict;
        if (total == 0)
        {
            verdict = "invalid";
        }
        else if ((float)payload.Yes / total > 0.5f)
        {
            verdict = "passed";
        }
        else
        {
            verdict = "rejected";
        }

        Chat.Server_ChatAnnouncement($"Yes {payload.Yes}, No {payload.No}, Abstain {payload.Ignore}");
        Chat.Server_ChatAnnouncement($"Vote result: {verdict}.");

        if (verdict == "passed")
        {
            AnnounceEffect(payload.Kind, payload.Payload ?? new Dictionary<string, string>());
        }
    }

    internal static void HandleRejected(VoteRejectedPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.CallerClientId)) return;

        if (ushort.TryParse(payload.CallerClientId, out ushort callerClientId)
            && ScavPlayer.TryGetPlayerFromClientId(callerClientId, out ScavPlayer caller))
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
                    Chat.Server_ChatAnnouncement($"{targetName} has been permanently banned.");
                }
                break;
        }
    }
}
