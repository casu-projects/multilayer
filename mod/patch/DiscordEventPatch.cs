using CasuMp.Protocol;
using CasuMod;
using HarmonyLib;
using Together;

namespace CasuMod.Patch;

public static class DiscordEventReporter
{
    internal static string CurrentLayer()
    {
        if (WorldGeneration.world == null) return "";
        return (WorldGeneration.world.biomeDepth + 1).ToString();
    }

    internal static void Send(string type, ScavPlayer plr)
    {
        if (plr == null || plr.IsLocal) return;
        OrchestratorClient.Instance?.SendEvent(type, new
        {
            playerKey = MigrationModule.PlayerKeyOf(plr),
            username = plr.playerName,
            layer = CurrentLayer(),
        });
    }
}

[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
internal static class ServerMain_OnPlayerDeath_DiscordReportPatch
{
    private static void Postfix(ScavPlayer plr)
    {
        DiscordEventReporter.Send(MessageTypes.Died, plr);
    }
}

[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.Server_RespawnCharacter))]
internal static class NetPlayer_Server_RespawnCharacter_DiscordReportPatch
{
    private static void Postfix(ScavPlayer __instance)
    {
        DiscordEventReporter.Send(MessageTypes.Respawned, __instance);
    }
}
