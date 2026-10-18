using HarmonyLib;
using Together;

namespace CasuMod.Patch;

internal static class JoinLeaveAnnounceMessage
{
    internal static string Build(ScavPlayer plr, string localeKey)
    {
        return Chat.AddColorTags(plr.playerColor, plr.playerName)
            + "<parsebreak> "
            + Chat.AddColorTags(Chat.DefaultChatColors.Server, $"<key:{localeKey}>");
    }
}

[HarmonyPatch(typeof(ScavPlayer), "Start")]
internal static class ScavPlayer_Start_AnnounceJoinPatch
{
    private static void Postfix(ScavPlayer __instance)
    {
        if (__instance == null || !Net.IsServer || __instance.IsLocal) return;

        if (MigrationArrivalTracker.ClientIds.Contains(__instance.playerId)) return;

        AnnounceRelay.SendJoin(__instance, JoinLeaveAnnounceMessage.Build(__instance, "plr_joined"));
    }
}

[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
internal static class ScavPlayer_OnDestroy_AnnounceLeavePatch
{
    private static void Prefix(ScavPlayer __instance)
    {
        if (__instance == null || !Net.IsServer || !Net.IsRunning || __instance.IsLocal) return;

        if (MigrationModule.IsFrozen(MigrationModule.PlayerKeyOf(__instance))) return;

        AnnounceRelay.SendLeave(__instance, JoinLeaveAnnounceMessage.Build(__instance, "plr_left"));
    }
}
