using CasuMp.Protocol;
using Together;

namespace CasuMod;

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

    private static void OnPlayerChat(ScavPlayer plr, string message)
    {
        if (plr == null || plr.playerName == "" || string.IsNullOrEmpty(message)) return;
        if (OrchestratorClient.Instance == null) return;
        if (ChatCommands.ChatModeCommand.GetMode(plr.playerId) == ChatCommands.ChatMode.Local) return; // Local - 릴레이 제외
        Color24 c = plr.playerColor;
        string color = $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        OrchestratorClient.Instance.SendEvent(MessageType.CHAT,
            new { speaker = plr.playerName, message = message, color = color });
    }

    internal static void Receive(ChatPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Message)) return;
        string speaker = payload.Speaker ?? "";
        if (speaker == "" || speaker == "*") return; // 시스템 공지 제외

        try
        {
            string name = BuildDisplayName(payload);
            _suppress = true; // 재브로드캐스트 루프 방지
            Chat.Server_ChatAnnouncement(name, null, payload.Message);
        }
        finally
        {
            _suppress = false;
        }
    }

    private static string BuildDisplayName(ChatPayload payload)
    {
        bool hasBadge = !string.IsNullOrEmpty(payload.Layer) || !string.IsNullOrEmpty(payload.Prefix); // 클라가 앞 '['/뒤 ']'를 붙임
        string badgeTag = string.IsNullOrEmpty(payload.Prefix) ? "" : BadgeTag(payload);
        string layerTag = string.IsNullOrEmpty(payload.Layer) ? "" : $"{payload.Layer}] ";
        string innerBracket = hasBadge ? "[" : "";
        string colorTag = string.IsNullOrEmpty(payload.Color) ? "" : $"<color={payload.Color}>";
        string closeTag = string.IsNullOrEmpty(payload.Color) ? "" : "</color>";
        return $"{badgeTag}{layerTag}{innerBracket}{colorTag}{payload.Speaker}{closeTag}";
    }

    private static string BadgeTag(ChatPayload payload)
    {
        string colorTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : $"<color={payload.PrefixColor}>";
        string closeTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : "</color>";
        return $"{colorTag}{payload.Prefix}{closeTag}] ";
    }
}
