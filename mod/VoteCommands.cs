using System;
using System.Collections.Generic;
using System.Globalization;
using Together;

namespace CasuMod;

internal static class VoteCommands
{
    private const float VoteTimeSeconds = 30f;

    internal static void CmdBanVote(ScavPlayer caller, string[] argv)
    {
        if (VoteSystem.Server_ActiveVote != null)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "A vote is already in progress. Please try again later.");
            return;
        }

        if (argv.Length < 2)
        {
            ChatCommands.ChatPrivateReply.SendToPlayer(caller, "Usage: !banvote <player name>.");
            return;
        }

        var payload = new Dictionary<string, string>
        {
            ["callerClientId"] = ((ushort)caller.playerId).ToString(CultureInfo.InvariantCulture),
            ["targetQuery"] = argv[1],
        };
        VoteRelay.EmitVoteStart(Guid.NewGuid().ToString(), "ban", "Permanent Ban Vote",
            promptBody: "", VoteTimeSeconds, payload);
    }
}
