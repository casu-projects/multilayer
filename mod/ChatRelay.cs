using Together;

namespace CasuMod;

// 크로스 인스턴스 채팅 - 플레이어 메시지만 릴레이한다 (시스템 공지 speaker="*" 제외)
// Chat.OnPlayerChatMessage(10099 수신기 내부 발화)로 수신해 오케스트레이터에 전달하고,
// 다른 인스턴스의 채팅은 "[L1] [닉네임]: 메시지" 형식으로 표시한다
public static class ChatRelay
{
    private static bool _suppress;
    private static bool _subscribed;

    // 플러그인 로드 시 1회 호출 - OnPlayerChatMessage 구독 (서버 인스턴스만)
    internal static void Init()
    {
        if (_subscribed) return;
        _subscribed = true;
        Chat.OnPlayerChatMessage += OnPlayerChat;
    }

    // 플레이어 채팅 (10099 수신기 내부 발화) -> 오케스트레이터 중계
    // 발신자의 닉네임 색상을 원본 유지하기 위해 ScavPlayer.playerColor를 HTML hex로 함께 보낸다
    private static void OnPlayerChat(ScavPlayer plr, string message)
    {
        if (plr == null || plr.playerName == "" || string.IsNullOrEmpty(message)) return;
        if (OrchestratorClient.Instance == null) return;
        if (ChatCommands.ChatModeCommand.GetMode(plr.playerId) == ChatCommands.ChatMode.Local) return; // Local - 릴레이 제외

        Color24 c = plr.playerColor;
        string color = $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        OrchestratorClient.Instance.SendEvent("CHAT",
            new { speaker = plr.playerName, message = message, color = color });
    }

    // 오케스트레이터로부터 수신한 다른 인스턴스의 플레이어 채팅 표시
    // 형식: "[L1] [<color=#hex>닉네임</color>]: 메시지", Discord 채팅은 "[D] [<color=#5865F2>유저명</color>]: 메시지"
    // 표시 경로(Server_ChatAnnouncement 3-arg)는 OnPlayerChatMessage를 발화시키지 않으므로
    // 루프가 구조적으로 불가능하며, _suppress는 안전망으로만 유지한다
    internal static void Receive(ChatPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Message)) return;
        string speaker = payload.Speaker ?? "";
        if (speaker == "" || speaker == "*") return; // 시스템 공지 - 후속 작업

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
        // 클라이언트가 이름을 "[" + name + "]: "로 감싸므로, 레이어/배지 태그는 앞 '['를 생략하고
        // 안쪽 '['만 넣어 "[L1] [이름]: 메시지"가 되게 한다 (태그 없으면 내부 '['도 생략).
        bool hasBadge = !string.IsNullOrEmpty(payload.Layer) || !string.IsNullOrEmpty(payload.Prefix);
        string badgeTag = string.IsNullOrEmpty(payload.Prefix) ? "" : BadgeTag(payload);
        string layerTag = string.IsNullOrEmpty(payload.Layer) ? "" : $"{payload.Layer}] ";
        string innerBracket = hasBadge ? "[" : "";
        string colorTag = string.IsNullOrEmpty(payload.Color) ? "" : $"<color={payload.Color}>";
        string closeTag = string.IsNullOrEmpty(payload.Color) ? "" : "</color>";
        return $"{badgeTag}{layerTag}{innerBracket}{colorTag}{payload.Speaker}{closeTag}";
    }

    // 배지 렌더 - "<color=#hex>D</color>] " (클라이언트가 앞 '['를 붙여 "[D] " 완성)
    private static string BadgeTag(ChatPayload payload)
    {
        string colorTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : $"<color={payload.PrefixColor}>";
        string closeTag = string.IsNullOrEmpty(payload.PrefixColor) ? "" : "</color>";
        return $"{colorTag}{payload.Prefix}{closeTag}] ";
    }
}
