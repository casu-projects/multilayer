using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>신규 플레이어 신원을 기존 클라이언트 전체에 즉시 브로드캐스트 (이전 모드 이식 + 레이스 수정).
/// 바닐라는 신원을 새 클라이언트에게만 보내고 기존 클라이언트는 스스로 10024 요청 왕복을 하길
/// 기대하는데, 바디가 CoolSync(불신뢰성)로 신원 왕복보다 먼저 동기화되어 기존 클라이언트가
/// "이름 없는 NPC 바디"를 먼저 생성한다. NPC 바디는 이후 신원이 도착해도 BodyToPlayerDict에
/// 등록되지 않아 (CharSync 재수신은 netBody.plr만 갱신) Shift 플레이어 위치 표시가 후발
/// 플레이어를 잡지 못한다 — 유저 목록(ClientIdToPlayerDict)에는 뜨지만 세계에는 안 보임.
///
/// 수정: 브로드캐스트를 NetPlayer.CreateCharacter **Prefix**에서 수행 — 바디가 생성되기 전에
/// 10023이 먼저 발신되어 클라이언트가 NetPlayer를 먼저 확보하고, 바디 도착 시 플레이어 바디로
/// 연결된다 (프레임 순서와 무관하게 결정적). Start Postfix도 유지 — 월드젠 대기(Start에서 바디
/// 미생성) 케이스와 이름/색 확정 재전송을 커버 (멱등 — 중복 수신은 이름/색 갱신만 함).</summary>

/// <summary>바디 생성 직전 — 10023이 바디 첫 동기화보다 항상 먼저 나가도록 (레이스 승리).
/// 이 시점에 playername/plrcolor/nameIsCustom은 CreatePlayer(ApplyNameAndColor)에서 이미 설정됨.</summary>
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

/// <summary>Start 종료 후 — 월드젠 대기 케이스(바디 지연 생성)와 이름/색 확정 재전송.</summary>
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
