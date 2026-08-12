using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

// 크로스 인스턴스 공지 릴레이 - 사망/퇴장/접속/마이그레이션 공지를 오케스트레이터 경유로
// 전 인스턴스(모든 레이어)에 전파한다. 바닐라 Server_ChatAnnouncement는 AllClientIds 고정이라
// 본인 제외가 불가능하므로 10098 와이어 미러로 타겟 지정 전송한다.
public static class AnnounceRelay
{
    public const string KindDeath = "death";
    public const string KindMigration = "migration";
    public const string KindLeave = "leave";
    public const string KindJoin = "join";

 // 사망 공지 발신 - 오케스트레이터가 전 인스턴스에 에코한다.
    internal static void SendDeath(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindDeath,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
        });
    }

 // 완전 퇴장 공지 발신 (마이그레이션/동결은 이 경로를 타지 않음).
    internal static void SendLeave(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindLeave,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
        });
    }

 // 신규 접속 공지 발신 (마이그레이션/재접속 도착은 제외). playerKey는 본인 제외용 -
 // 이름 해석 불가 시 빈 값으로 본인에게도 표시되는 폴백.
    internal static void SendJoin(string playerName, string playerKey)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindJoin,
            playerKey = playerKey,
            name = playerName,
        });
    }

 // 오케스트레이터 ANNOUNCE 수신 처리 (메인 스레드) - 본인 제외 공지.
    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Name)) return;

        switch (payload.Kind)
        {
            case KindDeath:
 // [*] 시스템 메시지 통일 (type 2, name="*") - 연한 빨강. 본인 제외
 // (개인 사망 안내는 별도 전송).
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#FF8080>{payload.Name}님이 사망했습니다.</color>");
                break;
            case KindLeave:
 // 완전 퇴장 - 본인 제외 전 클라이언트 공지, 노랑.
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#FFFF00>{payload.Name}님이 퇴장했습니다.</color>");
                break;
            case KindJoin:
 // 신규 접속 - 본인 제외 전 클라이언트 공지, 노랑.
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#FFFF00>{payload.Name}님이 접속했습니다.</color>");
                break;
            case KindMigration:
 // 레이어 이동 - 하늘색.
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#87CEEB>{payload.Name}님이 L{payload.FromDepth}에서 L{payload.ToDepth}로 이동합니다</color>");
                break;
        }
    }

 // 본인(playerKey) 제외 타겟 전송 - 바닐라 전체 고정 공지의 와이어 미러.
    private static void SendAnnouncementExcluding(string? playerKey, string message)
    {
        knetid? exclude = FindClientId(playerKey);
        List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != exclude)
            .ToList();
        if (targets.Count == 0) return;
        SendChatAnnouncementTo(message, targets);
    }

 // 개인 채팅 공지 (특정 클라이언트 1명).
    internal static void SendChatAnnouncementTo(string message, knetid clientId)
    {
        SendChatAnnouncementTo(message, new List<knetid> { clientId });
    }

 // 10098 타겟 전송 (type 2, name="*") - type 1은 클라이언트가 [*SERVER*]로 렌더링하므로
 // 시스템 메시지는 전부 type 2를 사용한다 ("[*]: 메시지" 표시).
    internal static void SendChatAnnouncementTo(string message, List<knetid> targets)
    {
        if (targets.Count == 0) return;
        var writer = Net.CreateWriter(10098);
        writer.Put((byte)2);
        writer.Put("*");
        writer.Put("");
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

 // playerKey(NAME_/STEAM_)로 이 인스턴스의 clientId 검색 (없으면 null - 다른 레이어).
    private static knetid? FindClientId(string? playerKey)
    {
        if (string.IsNullOrEmpty(playerKey)) return null;
        foreach (NetPlayer p in NetPlayer.ClientIdToPlayerDict.Values)
        {
            if (p.GetPersistentId() == playerKey)
                return p.clientId;
        }
        return null;
    }
}

// ANNOUNCE payload - death: name만 / migration: FromDepth/ToDepth/PlayerKey 포함.
public sealed class AnnouncePayload
{
    public string Kind { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    public int FromDepth { get; set; }
    public int ToDepth { get; set; }
}
