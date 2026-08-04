using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>크로스 인스턴스 채팅 (Phase 2) — 플레이어 메시지만 릴레이:
/// 베이스 모드의 공개 이벤트 Chat.OnPlayerChatMessage(NetPlayer, string)로 10099 플레이어
/// 채팅을 수신해 오케스트레이터로 전달(닉네임 색상 포함). 다른 인스턴스의 채팅은
/// "[L1] [<color>닉네임</color>]: 메시지" 형식으로 표시. 시스템 공지(speaker="*") 릴레이는
/// 후속 작업 (현재 제외).
/// 주의: 플레이어 채팅은 Server_ChatAnnouncement(3-arg)를 거치지 않고 10098 패킷을 직접
/// 보내므로, 반드시 이 이벤트(또는 Server_PlayerChatMessageSend 훅)를 사용해야 한다.</summary>
public static class ChatRelay
{
    private static bool _suppress;
    private static bool _subscribed;

    /// <summary>플러그인 로드 시 1회 호출 — OnPlayerChatMessage 구독 (서버 인스턴스만).</summary>
    internal static void Init()
    {
        if (_subscribed) return;
        _subscribed = true;
        Chat.OnPlayerChatMessage += OnPlayerChat;
    }

    /// <summary>플레이어 채팅 (10099 수신기 내부 발화) → 오케스트레이터 중계.
    /// 발신자의 닉네임 색상을 원본 유지하기 위해 NetPlayer.plrcolor를 HTML hex로 함께 보낸다.</summary>
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

    /// <summary>오케스트레이터로부터 수신한 다른 인스턴스의 플레이어 채팅 표시.
    /// 형식: "[L1] [<color=#hex>닉네임</color>]: 메시지". 표시 경로(Server_ChatAnnouncement
    /// 3-arg)는 OnPlayerChatMessage를 발화시키지 않으므로 루프가 구조적으로 불가능하며,
    /// _suppress는 안전망으로만 유지한다.</summary>
    internal static void Receive(ChatPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.Message)) return;
        string speaker = payload.Speaker ?? "";
        if (speaker == "" || speaker == "*") return; // 시스템 공지 — 후속 작업

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
        // 클라이언트가 이름을 "[" + name + "]: "로 감싸 표시한다 (ChatMsgContainer.Compile).
        // 최종 표시 "[L1] [player2222]: message"를 만들기 위해 레이어 태그의 앞 '['는
        // 생략하고, 안쪽 '['는 포함하되 닫는 ']'는 넣지 않는다 — 클라이언트가 앞 '['와
        // 뒤 ']'를 각각 하나씩 붙인다 ("L1] [<color>이름</color>" → "[L1] [이름]: ").
        // 레이어가 없으면(Discord 관리자 채팅 등) 내부 '['도 생략 — 클라이언트가 붙이는
        // 단일 '['만으로 "[관리자]: 메시지"가 완성된다 (이중 대괄호 방지).
        string layerTag = string.IsNullOrEmpty(payload.Layer) ? "" : $"{payload.Layer}] ";
        string innerBracket = string.IsNullOrEmpty(payload.Layer) ? "" : "[";
        string colorTag = string.IsNullOrEmpty(payload.Color) ? "" : $"<color={payload.Color}>";
        string closeTag = string.IsNullOrEmpty(payload.Color) ? "" : "</color>";
        return $"{layerTag}{innerBracket}{colorTag}{payload.Speaker}{closeTag}";
    }
}
