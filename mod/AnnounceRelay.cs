using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

public static class AnnounceRelay
{
    public const string KindDeath = "death";
    public const string KindMigration = "migration";
    public const string KindLeave = "leave";
    public const string KindJoin = "join";
    public const string KindRespawn = "respawn";

    internal static void SendJoin(ScavPlayer plr, string text)
    {
        SendLocalAndRelay(KindJoin, plr, text);
    }

    internal static void SendLeave(ScavPlayer plr, string text)
    {
        SendLocalAndRelay(KindLeave, plr, text);
    }

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

    private static void SendLocalAndRelay(string kind, ScavPlayer plr, string text)
    {
        if (plr == null || string.IsNullOrEmpty(text)) return;
        SendChatAnnouncementTo(text, new List<knetid>(ServerMain.AllClientIds)); // 발신 레이어 즉시 표시
        OrchestratorClient.Instance?.SendEvent("ANNOUNCE", new // 타 레이어 릴레이
        {
            kind,
            playerKey = MigrationModule.PlayerKeyOf(plr),
            name = plr.playerName,
            originInstance = OrchestratorClient.Instance.InstanceKey,
            text,
        });
    }

    internal static void Handle(AnnouncePayload payload)
    {
        if (payload == null) return;

        switch (payload.Kind)
        {
            case KindDeath:
            case KindJoin:
            case KindLeave:
                if (!string.IsNullOrEmpty(payload.OriginInstance)
                    && OrchestratorClient.Instance != null
                    && payload.OriginInstance == OrchestratorClient.Instance.InstanceKey)
                {
                    return; // 발신 레이어는 제외(이미 즉시 표시됨)
                }
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

    private static void SendAnnouncementExcluding(string playerKey, string message)
    {
        knetid? exclude = FindClientId(playerKey);
        List<knetid> targets = ScavPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != exclude)
            .ToList();
        if (targets.Count == 0) return;
        SendChatAnnouncementTo(message, targets);
    }

    internal static void SendChatAnnouncementTo(string message, knetid clientId)
    {
        SendChatAnnouncementTo(message, new List<knetid> { clientId });
    }

    internal static void SendChatAnnouncementTo(string message, List<knetid> targets)
    {
        if (targets.Count == 0) return;
        var writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_ChatMessage); // type 2, name="*" 와이어
        writer.Put((byte)2);
        writer.Put("*");
        writer.Put("");
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

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
