using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

// 크로스 인스턴스 공지 릴레이 - 사망/퇴장/접속/레이어 이동/리스폰 공지를 오케스트레이터 경유로
// 전 레이어에 전파한다. base 의 Server_ChatAnnouncement 는 AllClientIds 고정이라 타겟 지정이
// 불가능해 CLIENT_ChatMessage(type 2, name="*") 와이어로 전송한다.
//
// 접속/퇴장: base 로컬 공지(<key:plr_joined>/<key:plr_left>)는 JoinLeaveAnnounceSuppressPatch 가
// 억제하고, 모드가 base 와 동일한 포맷/색상으로 직접 만들어 "발신 레이어 즉시 표시 + 타 레이어
// 크로스 전송"한다. 레이어 이동(마이그레이션)은 JoinLeaveAnnouncePatch 가 이벤트를 만들지 않아
// 자연히 표시되지 않는다.
// 사망: base 의 강화된 로컬 메시지(사유 포함)가 발신 레이어 담당, 이 릴레이는 타 레이어에 원문 전달.
public static class AnnounceRelay
{
    public const string KindDeath = "death";
    public const string KindMigration = "migration";
    public const string KindLeave = "leave";
    public const string KindJoin = "join";
    public const string KindRespawn = "respawn";

    // 접속 공지 - base 와 동일 포맷. 발신 레이어 즉시 표시 + 타 레이어 크로스 전송
    internal static void SendJoin(ScavPlayer plr, string text)
    {
        SendLocalAndRelay(KindJoin, plr, text);
    }

    // 퇴장 공지 - base 와 동일 포맷
    internal static void SendLeave(ScavPlayer plr, string text)
    {
        SendLocalAndRelay(KindLeave, plr, text);
    }

    // 사망 공지 - text 는 base 가 생성한 사망 사유 포함 원문(태그 해석 완료).
    // 발신 레이어는 base 로컬 공지가 담당하므로 로컬 표시 없이 타 레이어에만 전송한다
    internal static void SendDeath(ScavPlayer plr, string text)
    {
        if (plr == null || string.IsNullOrEmpty(text)) return;
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind = KindDeath,
            playerKey = MigrationModule.PlayerKeyOf(plr),
            name = plr.playerName,
            originInstance = OrchestratorClient.Instance.InstanceKey,
            text,
        });
    }

    // 로컬 즉시 표시 + 오케스트레이터로 전 레이어 릴레이 (발신 레이어는 Handle 에서 제외)
    private static void SendLocalAndRelay(string kind, ScavPlayer plr, string text)
    {
        if (plr == null || string.IsNullOrEmpty(text)) return;
        SendChatAnnouncementTo(text, new List<knetid>(ServerMain.AllClientIds));
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new
        {
            kind,
            playerKey = MigrationModule.PlayerKeyOf(plr),
            name = plr.playerName,
            originInstance = OrchestratorClient.Instance.InstanceKey,
            text,
        });
    }

    // 오케스트레이터 ANNOUNCE 수신 처리 (메인 스레드) - 본인 제외 공지
    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null) return;

        switch (payload.Kind)
        {
            case KindDeath:
            case KindJoin:
            case KindLeave:
                // 발신 레이어는 이미 로컬로 표시했다 - 중복 방지
                if (!string.IsNullOrEmpty(payload.OriginInstance)
                    && OrchestratorClient.Instance != null
                    && payload.OriginInstance == OrchestratorClient.Instance.InstanceKey)
                {
                    return;
                }
                // join/leave/death 는 base 포맷 원문(text)을 그대로 사용
                if (string.IsNullOrEmpty(payload.Text)) return;
                SendAnnouncementExcluding(payload.PlayerKey, payload.Text);
                break;
            case KindMigration:
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#87CEEB>{payload.Name} moved from L{payload.FromDepth} to L{payload.ToDepth}.</color>");
                break;
            case KindRespawn:
                SendAnnouncementExcluding(payload.PlayerKey,
                    $"<color=#90EE90>{payload.Name} respawned.</color>");
                break;
        }
    }

    // 본인(playerKey) 제외 타겟 전송 - 바닐라 전체 고정 공지의 와이어 미러
    private static void SendAnnouncementExcluding(string playerKey, string message)
    {
        knetid? exclude = FindClientId(playerKey);
        List<knetid> targets = ScavPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != exclude)
            .ToList();
        if (targets.Count == 0) return;
        SendChatAnnouncementTo(message, targets);
    }

    // 개인 채팅 공지 (특정 클라이언트 1명)
    internal static void SendChatAnnouncementTo(string message, knetid clientId)
    {
        SendChatAnnouncementTo(message, new List<knetid> { clientId });
    }

    // CLIENT_ChatMessage type 2 (name="*") 타겟 전송 - 시스템 메시지 통일 ("[*]: 메시지" 표시)
    // 클라가 Lang.MsgTryParse 로 <key:>/<parsebreak> 를 해석한다 (base 로케일/포맷 그대로 표시)
    internal static void SendChatAnnouncementTo(string message, List<knetid> targets)
    {
        if (targets.Count == 0) return;
        var writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_ChatMessage);
        writer.Put((byte)2);
        writer.Put("*");
        writer.Put("");
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    // playerKey(NAME_/STEAM_)로 이 인스턴스의 clientId 검색 (없으면 null - 다른 레이어)
    private static knetid? FindClientId(string playerKey)
    {
        if (string.IsNullOrEmpty(playerKey)) return null;
        foreach (ScavPlayer p in ScavPlayer.ClientIdToPlayerDict.Values)
        {
            if (MigrationModule.PlayerKeyOf(p) == playerKey || p.GetPersistentId() == playerKey)
                return p.playerId;
        }
        return null;
    }
}

// ANNOUNCE payload - death/join/leave: Text(base 포맷 원문) / migration: FromDepth/ToDepth 포함
// originInstance: 발신 레이어 (해당 레이어는 로컬 표시가 담당 - 중복 방지)
public sealed class AnnouncePayload
{
    public string Kind { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Name { get; set; } = "";
    public int FromDepth { get; set; }
    public int ToDepth { get; set; }
    public string OriginInstance { get; set; } = "";
    public string Text { get; set; } = "";
}
