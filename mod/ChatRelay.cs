using System.Collections.Generic;
using System.Linq;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 크로스 인스턴스 채팅 릴레이 — 플레이어 메시지를 오케스트레이터로 중계하고, 다른 인스턴스
// 채팅을 "[L1] [닉네임]: 메시지" 형식으로 표시한다.
public static class ChatRelay
{
    private static bool _suppress;
    private static bool _subscribed;

    internal static void Init()
    {
        if (_subscribed) return;
        _subscribed = true;
        Chat.OnPlayerChatMessage += OnPlayerChat;
    }

    // 플레이어 채팅 → 오케스트레이터 중계 (닉네임 색상 포함).
    private static void OnPlayerChat(NetPlayer plr, string message)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (plr == null || plr.playername == "" || string.IsNullOrEmpty(message)) return;
        if (OrchestratorClient.Instance == null) return;
        if (ChatCommands.ChatModeCommand.GetMode(plr.clientId) == ChatCommands.ChatMode.Local) return; // Local — 릴레이 제외

        Color24 c = plr.plrcolor;
        string color = $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        OrchestratorClient.Instance.SendEvent("CHAT",
            new { speaker = plr.playername, message = message, color = color });
    }

    // 수신 채팅 표시. 표시 경로는 OnPlayerChatMessage를 발화시키지 않아 루프가 없으며,
    // _suppress는 안전망으로만 유지한다.
    internal static void Receive(ChatPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Message)) return;
        string speaker = payload.Speaker ?? "";
        if (speaker == "" || speaker == "*") return; // 시스템 공지 — 제외

        // 그룹 채팅 — targets(멤버 이름)로 대상 지정 표시 (발신자 제외).
        if (payload.Targets != null && payload.Targets.Length > 0)
        {
            ShowGroupChat(payload);
            return;
        }

        try
        {
            string name = BuildDisplayName(payload);
            _suppress = true;
            Chat.Server_ChatAnnouncement(name, null, payload.Message);
        }
        finally
        {
            _suppress = false;
        }
    }

    // 그룹 채팅 표시 — 이 인스턴스의 대상 멤버에게 타겟 전송 ("[그룹명] [L1] [이름]: 메시지").
    // 발신자도 포함된다 (그룹 모드는 원본 브로드캐스트가 억제되므로 중복 없음).
    private static void ShowGroupChat(ChatPayload payload)
    {
        string name = BuildDisplayName(payload);
        var targets = new List<knetid>();
        foreach (NetPlayer p in NetPlayer.ClientIdToPlayerDict.Values)
        {
            if (payload.Targets.Contains(p.playername)) targets.Add(p.clientId);
        }
        if (targets.Count == 0) return;
        AnnounceRelay.SendChatAnnouncementToNamed(name, payload.Message, targets);
    }

    private static string BuildDisplayName(ChatPayload payload)
    {
        // 클라이언트가 name을 "[" + name + "]: "로 감싼다. 첫 태그는 클라이언트의 선행 '['
        // 로 열리고, 이후 태그는 자체 '['를 붙여 "[그룹명] [L1] [이름]" 형태를 완성한다.
        var tags = new List<string>();
        if (!string.IsNullOrEmpty(payload.Prefix)) tags.Add(BadgeTag(payload));
        if (!string.IsNullOrEmpty(payload.Badge)) tags.Add(Badge2Tag(payload));
        if (!string.IsNullOrEmpty(payload.Layer)) tags.Add($"{payload.Layer}] ");

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) sb.Append('[');
            sb.Append(tags[i]);
        }
        if (tags.Count > 0) sb.Append('[');
        string colorTag = string.IsNullOrEmpty(payload.Color) ? "" : $"<color={payload.Color}>";
        string closeTag = string.IsNullOrEmpty(payload.Color) ? "" : "</color>";
        sb.Append(colorTag);
        sb.Append(payload.Speaker);
        sb.Append(closeTag);
        return sb.ToString();
    }

    // 배지 렌더 — "<color>D</color>] " (클라이언트가 앞 '['를 붙여 "[D] " 완성).
    private static string BadgeTag(ChatPayload payload)
    {
        string colorTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : $"<color={payload.PrefixColor}>";
        string closeTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : "</color>";
        return $"{colorTag}{payload.Prefix}{closeTag}] ";
    }

    // 두 번째 배지 렌더 — Discord 그룹 채팅의 "D" 배지.
    private static string Badge2Tag(ChatPayload payload)
    {
        string colorTag = string.IsNullOrEmpty(payload.BadgeColor) ? "" : $"<color={payload.BadgeColor}>";
        string closeTag = string.IsNullOrEmpty(payload.BadgeColor) ? "" : "</color>";
        return $"{colorTag}{payload.Badge}{closeTag}] ";
    }
}
