using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 사망/리스폰 이벤트 → 오케스트레이터 보고 (Discord 알림용).
public static class DiscordEventReporter
{
    // 현재 레이어 (1-based) — 월드 생성 전이면 빈 문자열.
    internal static string CurrentLayer()
    {
        if (WorldGeneration.world == null) return "";
        return (WorldGeneration.world.biomeDepth + 1).ToString();
    }

    internal static void Send(string type, NetPlayer plr)
    {
        if (plr == null || plr.is_local) return;
        OrchestratorClient.Instance?.SendEvent(type, new
        {
            playerKey = plr.GetPersistentId(),
            username = plr.playername,
            layer = CurrentLayer(),
        });
    }
}

// 사망 — ServerMain.OnPlayerDeath.
[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
internal static class ServerMain_OnPlayerDeath_DiscordReportPatch
{
    private static void Postfix(NetPlayer plr)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        DiscordEventReporter.Send("DIED", plr);
    }
}

// 리스폰 — Server_RespawnCharacter.
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.Server_RespawnCharacter))]
internal static class NetPlayer_Server_RespawnCharacter_DiscordReportPatch
{
    private static void Postfix(NetPlayer __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        DiscordEventReporter.Send("RESPAWNED", __instance);
    }
}
