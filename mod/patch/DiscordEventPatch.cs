using CasuMod;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

// 사망/리스폰 이벤트를 채팅 텍스트 파싱 대신 직접 훅해 오케스트레이터에 보고 (Discord 알림용)
// 오케스트레이터는 마이그레이션 중 이벤트를 스킵해 마이그레이션 알림과의 중복을 방지한다
public static class DiscordEventReporter
{
    // 현재 레이어 (1-based) - 월드 생성 전이면 빈 문자열
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

// 사망 - ServerMain.OnPlayerDeath (서버측 사망 확정 지점)
[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.OnPlayerDeath))]
internal static class ServerMain_OnPlayerDeath_DiscordReportPatch
{
    private static void Postfix(NetPlayer plr)
    {
        DiscordEventReporter.Send("DIED", plr);
    }
}

// 리스폰 - Server_RespawnCharacter (리스폰/마이그레이션 목적지 신규 생성 포함)
// 오케스트레이터가 마이그레이션 중 이벤트를 필터링한다
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.Server_RespawnCharacter))]
internal static class NetPlayer_Server_RespawnCharacter_DiscordReportPatch
{
    private static void Postfix(NetPlayer __instance)
    {
        DiscordEventReporter.Send("RESPAWNED", __instance);
    }
}
