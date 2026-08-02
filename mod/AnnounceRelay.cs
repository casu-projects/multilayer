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

    /// <summary>오케스트레이터로부터 수신한 ANNOUNCE 처리 (메인 스레드 — OrchestratorClient
    /// 인바운드 큐). death: 전 클라이언트 채팅 공지 / migration: 본인 제외 타겟 공지.</summary>
    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Name)) return;

        switch (payload.Kind)
        {
            case KindDeath:
                Chat.Server_ChatAnnouncement($"{payload.Name}님이 사망하였습니다.");
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

    /// <summary>10098 채팅 공지 타겟 전송 — Chat.Server_ChatAnnouncement의 와이어 미러
    /// ([type 1B][message] → 지정 수신자).</summary>
    internal static void SendChatAnnouncementTo(string message, List<knetid> targets)
    {
        if (targets.Count == 0) return;
        var writer = Net.CreateWriter(10098);
        writer.Put((byte)1);
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
