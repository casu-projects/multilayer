using CasuMp.Protocol;

namespace CasuMpServer;

internal sealed class VoteService
{
    private readonly ControlHub _hub;
    private readonly ClientGateway _gateway;
    private readonly PlayerSessionStore _sessions;
    private readonly VoteCoordinator _votes;
    private readonly BanList _banList;

    public VoteService(ControlHub hub, ClientGateway gateway, PlayerSessionStore sessions, VoteCoordinator votes, BanList banList)
    {
        _hub = hub;
        _gateway = gateway;
        _sessions = sessions;
        _votes = votes;
        _banList = banList;
    }

    public void Tick()
    {
        if (_votes.TryFinalize(DateTime.UtcNow, out VoteCoordinator.VoteFinalizeResult result))
        {
            Finalize(result);
        }
    }

    public void HandleStart(ControlHub.ClientConnection conn, VoteStartMarker marker)
    {
        string? callerClientId = marker.Payload.GetValueOrDefault("callerClientId");

        if (marker.Kind == "ban")
        {
            string targetQuery = marker.Payload.GetValueOrDefault("targetQuery", "");
            if (!TryResolveOnlinePlayer(targetQuery, out string resolvedKey, out string resolvedName))
            {
                Reject(conn, marker, callerClientId, $"Player not found: {targetQuery}");
                return;
            }
            marker.Payload["targetName"] = resolvedName;
            marker.Payload["targetKey"] = resolvedKey;
            marker.PromptBody = resolvedName;
        }

        var expected = _hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed)
            .Select(c => c.InstanceKey ?? "")
            .Where(k => k.Length > 0)
            .ToList();

        if (!_votes.TryStart(marker, expected))
        {
            Reject(conn, marker, callerClientId, "A vote is already in progress. Please try again later.");
            return;
        }

        foreach (var other in _hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            _hub.SendNoAck(other, MessageTypes.VoteRun, new
            {
                voteId = marker.VoteId,
                kind = marker.Kind,
                title = marker.Title,
                promptBody = marker.PromptBody,
                timeoutSeconds = marker.TimeoutSeconds,
                payload = marker.Payload,
            });
        }
    }

    public void HandleTally(ControlHub.ClientConnection conn, VoteTallyMarker marker)
    {
        _votes.RecordTally(marker, conn.InstanceKey ?? "");
    }

    private void Reject(ControlHub.ClientConnection conn, VoteStartMarker marker,
        string? callerClientId, string reason)
    {
        if (!string.IsNullOrEmpty(callerClientId))
        {
            _hub.SendNoAck(conn, MessageTypes.VoteRejected, new { callerClientId, reason });
        }
    }

    private bool TryResolveOnlinePlayer(string query, out string playerKey, out string name)
    {
        playerKey = "";
        name = "";
        var match = _sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .FirstOrDefault(s =>
                (s.Username != null && s.Username.Equals(query, StringComparison.OrdinalIgnoreCase))
                || s.Key.Value.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;

        playerKey = match.Key.Value;
        name = match.Username ?? match.Key.Value;
        return true;
    }

    private void Finalize(VoteCoordinator.VoteFinalizeResult result)
    {
        foreach (var conn in _hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            _hub.SendNoAck(conn, MessageTypes.VoteResult, new
            {
                voteId = result.VoteId,
                kind = result.Kind,
                yes = result.Yes,
                no = result.No,
                ignore = result.Ignore,
                payload = result.Payload,
            });
        }

        int totalVotes = result.Yes + result.No + result.Ignore;
        bool votePassed = totalVotes > 0 && (float)result.Yes / totalVotes > 0.5f;
        if (!votePassed) return;

        if (result.Kind == "ban")
        {
            string? targetKey = result.Payload.GetValueOrDefault("targetKey");
            if (!string.IsNullOrEmpty(targetKey))
            {
                _banList.Toggle(targetKey, true);
                _gateway.SetBanned(PlayerKey.FromString(targetKey), true);
                Log.Info($"[Vote] 투표 가결 — {targetKey} 밴 처리 (파일 + 차단/킥).");
            }
        }
    }
}
