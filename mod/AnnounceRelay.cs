using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

/// <summary>크로스 인스턴스 공지 릴레이 (2026-08-03) — 사망/마이그레이션 공지를
/// 오케스트레이터를 경유해 전 인스턴스(모든 레이어)에 전파한다.
/// 발신: 모드 → 오케스트레이터 "ANNOUNCE" → 전 인스턴스 에코 (발신 포함).
/// 표시: 바닐라 10098(채팅 공지) — 타겟 지정 전송은 와이어 미러 (Server_ChatAnnouncement는
/// AllClientIds 고정이라 사망자/마이그레이션 본인 제외가 불가능).</summary>
public static class AnnounceRelay
{
    public const string KindDeath = "death";
    public const string KindMigration = "migration";
    public const string KindLeave = "leave";
    public const string KindJoin = "join";

    /// <summary>사망 공지 발신 (ServerMain.OnPlayerDeath 대체 패치에서 호출) —
    /// 오케스트레이터가 전 인스턴스에 에코한다.</summary>
    internal static void SendDeath(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindDeath,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
        });
    }

    /// <summary>완전 퇴장 공지 발신 (NetPlayer_OnDestroy — 비동결 퇴장 시) —
    /// 오케스트레이터가 전 인스턴스에 에코한다. 마이그레이션(동결)은 이 경로를 타지 않는다.</summary>
    internal static void SendLeave(NetPlayer plr)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindLeave,
            playerKey = plr.GetPersistentId(),
            name = plr.playername,
        });
    }

    /// <summary>신규 접속 공지 발신 (Server_ChatAnnouncement 접속 번역 시) —
    /// 오케스트레이터가 전 인스턴스에 에코한다 (발신 레이어 포함).
    /// 마이그레이션/재접속 도착은 IsMigrationArrivalJoin 억제로 이 경로를 타지 않는다.</summary>
    internal static void SendJoin(string playerName)
    {
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindJoin,
            playerKey = "",
            name = playerName,
        });
    }

    /// <summary>오케스트레이터로부터 수신한 ANNOUNCE 처리 (메인 스레드 — OrchestratorClient
    /// 인바운드 큐). death: 전 클라이언트 채팅 공지 / migration: 본인 제외 타겟 공지.</summary>
    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Name)) return;

        switch (payload.Kind)
        {
            case KindDeath:
                // [*] 시스템 메시지 통일 — 1-arg Server_ChatAnnouncement(바닐라 [*SERVER*] 경로)
                // 대신 type 2 name="*" 전송 (2026-08-03).
                SendChatAnnouncementTo($"{payload.Name}님이 사망하였습니다.",
                    new List<knetid>(NetPlayer.ClientIdToPlayerDict.Keys));
                break;
            case KindLeave:
                // 완전 퇴장 — 전 클라이언트 공지 (발신 레이어 포함 — 바닐라는 퇴장 공지가 없다).
                SendChatAnnouncementTo($"{payload.Name}님이 퇴장하였습니다.",
                    new List<knetid>(NetPlayer.ClientIdToPlayerDict.Keys));
                break;
            case KindJoin:
                // 신규 접속 — 전 클라이언트 공지 (발신 레이어 포함 — 에코로 표시).
                SendChatAnnouncementTo($"{payload.Name}님이 접속하였습니다.",
                    new List<knetid>(NetPlayer.ClientIdToPlayerDict.Keys));
                break;
            case KindMigration:
                string text = $"{payload.Name}님이 L{payload.FromDepth}에서 L{payload.ToDepth}로 이동합니다";
                // 본인 제외 — 바닐라 Server_ChatAnnouncement는 전체 고정이라 와이어 미러로 타겟 지정.
                knetid? exclude = FindClientId(payload.PlayerKey);
                List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
                    .Where(id => id != exclude)
                    .ToList();
                if (targets.Count == 0) break;
                SendChatAnnouncementTo(text, targets);
                break;
        }
    }

    /// <summary>개인 채팅 공지 (사망 안내 등 — 특정 클라이언트 1명).</summary>
    internal static void SendChatAnnouncementTo(string message, knetid clientId)
    {
        SendChatAnnouncementTo(message, new List<knetid> { clientId });
    }

    /// <summary>10098 채팅 공지 타겟 전송 — [*] 시스템 메시지 통일 (type 2, name="*").
    /// type 1(이름 없음)은 클라이언트가 [*SERVER*]로 렌더링하므로, 시스템 메시지는 전부
    /// type 2 name="*" — "[*]: 메시지" (ChatMsgContainer.Compile이 이름을 대괄호로 감쌈).
    /// !list/!currentrun 개인 회신(ChatPrivateReply)과 동일 포맷 (2026-08-03 통일).</summary>
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

    /// <summary>playerKey(NAME_/STEAM_)로 이 인스턴스의 clientId 검색 (없으면 null — 다른 레이어).</summary>
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

/// <summary>ANNOUNCE payload — death: name만 / migration: FromDepth/ToDepth/PlayerKey 포함.</summary>
public sealed class AnnouncePayload
{
    public string Kind { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    public int FromDepth { get; set; }
    public int ToDepth { get; set; }
}
