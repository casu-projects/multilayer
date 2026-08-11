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

    private static string BuildDisplayName(ChatPayload payload)
    {
        // 클라이언트가 이름을 "[" + name + "]: "로 감싸므로, "[L1] [이름]: "을 만들기 위해
        // 레이어 태그의 앞 '['는 생략하고 내부 '['만 넣는다 ("L1] [이름" → 클라가 "[L1] [이름]: ").
        bool hasBadge = !string.IsNullOrEmpty(payload.Layer) || !string.IsNullOrEmpty(payload.Prefix);
        string badgeTag = string.IsNullOrEmpty(payload.Prefix) ? "" : BadgeTag(payload);
        string layerTag = string.IsNullOrEmpty(payload.Layer) ? "" : $"{payload.Layer}] ";
        string innerBracket = hasBadge ? "[" : "";
        string colorTag = string.IsNullOrEmpty(payload.Color) ? "" : $"<color={payload.Color}>";
        string closeTag = string.IsNullOrEmpty(payload.Color) ? "" : "</color>";
        return $"{badgeTag}{layerTag}{innerBracket}{colorTag}{payload.Speaker}{closeTag}";
    }

    // 배지 렌더 — "<color>D</color>] " (클라이언트가 앞 '['를 붙여 "[D] " 완성).
    private static string BadgeTag(ChatPayload payload)
    {
        string colorTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : $"<color={payload.PrefixColor}>";
        string closeTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : "</color>";
        return $"{colorTag}{payload.Prefix}{closeTag}] ";
    }
}
