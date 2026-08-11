using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 신규 플레이어 신원(10023)을 기존 클라이언트 전체에 즉시 브로드캐스트 — 바디 CoolSync가
// 신원 왕복보다 먼저 도착해 "이름 없는 NPC 바디"가 생기는 레이스를 차단한다.
// CreateCharacter Prefix(바디 생성 전 발신) + Start Postfix(월드젠 대기 케이스) 이중 커버.
[HarmonyPatch(typeof(NetPlayer), "CreateCharacter")]
internal static class NetPlayer_CreateCharacter_BroadcastNewPlayerPatch
{
    private static void Prefix(NetPlayer __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance.is_local)
            return;

        RosterBroadcast.SendIdentity(__instance, "CreateCharacter");
    }
}

[HarmonyPatch(typeof(NetPlayer), "Start")]
internal static class NetPlayer_Start_BroadcastNewPlayerPatch
{
    private static void Postfix(NetPlayer __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance.is_local)
            return;

        RosterBroadcast.SendIdentity(__instance, "Start");
    }
}

internal static class RosterBroadcast
{
    internal static void SendIdentity(NetPlayer player, string source)
    {
        List<knetid> others = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != player.clientId)
            .ToList();

        if (others.Count == 0)
            return;

        player.Server__ResponsePlayerName(others);
    }
}
