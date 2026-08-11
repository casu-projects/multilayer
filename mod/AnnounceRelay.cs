using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

// 크로스 인스턴스 공지 릴레이 — 사망/퇴장/접속/마이그레이션 공지를 오케스트레이터 경유로
// 전 인스턴스(모든 레이어)에 전파한다.
public static class AnnounceRelay
{
    public const string KindDeath = "death";
    public const string KindMigration = "migration";
    public const string KindLeave = "leave";
    public const string KindJoin = "join";

    internal static void SendDeath(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindDeath,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
        });
    }

    internal static void SendLeave(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindLeave,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
            clientId = (int)plr.clientId,
        });
    }

    internal static void SendJoin(string playerName, string playerKey)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindJoin,
            playerKey = playerKey,
            name = playerName,
        });
    }

    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Name)) return;

        switch (payload.Kind)
        {
            case KindDeath:
                SendAnnouncementExcluding(payload.PlayerKey, payload.Name, payload.ClientId,
                    $"<color=#FF8080>{payload.Name}님이 사망했습니다.</color>");
                break;
            case KindLeave:
                SendAnnouncementExcluding(payload.PlayerKey, payload.Name, payload.ClientId,
                    $"<color=#FFFF00>{payload.Name}님이 퇴장했습니다.</color>");
                break;
            case KindJoin:
                SendAnnouncementExcluding(payload.PlayerKey, payload.Name, payload.ClientId,
                    $"<color=#FFFF00>{payload.Name}님이 접속했습니다.</color>");
                break;
            case KindMigration:
                SendAnnouncementExcluding(payload.PlayerKey, payload.Name, payload.ClientId,
                    $"<color=#87CEEB>{payload.Name}님이 L{payload.FromDepth}에서 L{payload.ToDepth}로 이동합니다</color>");
                break;
        }
    }

    // playerKey 대상(본인)을 제외한 타겟으로 전송.
    // 제외 판정 폴백: clientId(퇴장 — dict에서 이미 제거됨) > playerKey(일반) > 이름(접속 —
    // 등록 전에 발화되어 playerKey가 비어 있음).
    private static void SendAnnouncementExcluding(string? playerKey, string? name, int clientId, string message)
    {
        knetid? exclude = null;
        if (clientId > 0) exclude = (knetid)(ushort)clientId;
        if (exclude == null) exclude = FindClientId(playerKey);
        if (exclude == null && !string.IsNullOrEmpty(name)) exclude = FindClientIdByName(name);
        List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != exclude)
            .ToList();
        if (targets.Count == 0) return;
        SendChatAnnouncementTo(message, targets);
    }

    internal static void SendChatAnnouncementTo(string message, knetid clientId)
    {
        SendChatAnnouncementTo(message, new List<knetid> { clientId });
    }

    // 10098 타겟 전송 — type 2 name="*" ([*] 시스템 메시지 형식).
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

    // 10098 타겟 전송 — name 지정 (그룹 채팅: "[그룹명] [L1] [이름]" 배지 렌더).
    internal static void SendChatAnnouncementToNamed(string name, string message, List<knetid> targets)
    {
        if (targets.Count == 0) return;
        var writer = Net.CreateWriter(10098);
        writer.Put((byte)2);
        writer.Put(name);
        writer.Put("");
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    // playerKey로 이 인스턴스의 clientId 검색 (없으면 null — 다른 레이어).
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

public sealed class AnnouncePayload
{
    public string Kind { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    // 대상 clientId — 퇴장 공지처럼 대상이 dict에서 제거된 뒤 도착하는 경우를 위한 폴백.
    public int ClientId { get; set; } = -1;
    public int FromDepth { get; set; }
    public int ToDepth { get; set; }
}
