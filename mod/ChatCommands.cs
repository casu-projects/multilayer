using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

// 채팅 명령 시스템 — "!" 접두사 명령 라우터. 10099 수신기를 가로채 명령이면 원본
// (브로드캐스트/릴레이)을 스킵하고 로컬 핸들러로 처리한다. 크로스 인스턴스가 필요한
// 명령(!list/!currentrun/!calladmin 등)은 오케스트레이터로 요청해 개인 회신받는다.
public static class ChatCommands
{
    private const string CommandPrefix = "!";

    [HarmonyPatch(typeof(Chat), "Server_PlayerChatMessageSend")]
    internal static class Chat_CommandRouterPatch
    {
        private static bool Prefix(knetid clientId, ref NetDataReader reader)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;

            int startPosition = reader.Position;
            reader.Get(out bool _unusedFlag);
            reader.Get(out string message);

            if (string.IsNullOrEmpty(message) || !message.StartsWith(CommandPrefix))
            {
                reader.SetPosition(startPosition); // 원본이 정상 읽도록 복원
                return true;
            }

            if (!NetPlayer.TryGetPlayerFromClientId(clientId, out NetPlayer plr))
            {
                return false;
            }

            string command = message.Substring(CommandPrefix.Length);
            string[] argv = command.Trim().Split(' ');
            if (argv.Length > 0)
            {
                if (HelpCommand.TryHandle(plr, argv)
                    || ChatModeCommand.TryHandle(plr, argv)
                    || PlayerListCommand.TryHandle(plr, argv)
                    || CurrentRunCommand.TryHandle(plr, argv)
                    || CallAdminCommand.TryHandle(plr, argv)
                    || DiscordCommand.TryHandle(plr, argv)
                    || RespawnCommand.TryHandle(plr, argv)
                    || VoteCommands.TryHandle(plr, argv))
                {
                    return false;
                }

                // 게임 어드민 명령 dict 폴백.
                string cmdName = argv[0];
                var cmdDict = AccessTools.Field(typeof(ServerMain), "ServerClientCustomCommandsDict")
                    ?.GetValue(null) as Dictionary<string, Action<NetPlayer, string, string[]>>;
                if (cmdDict != null && cmdDict.ContainsKey(cmdName))
                {
                    AccessTools.Method(typeof(ServerMain), "RunClientCustomCommand")
                        ?.Invoke(null, new object[] { command, plr });
                }
                else
                {
                    ChatPrivateReply.SendToPlayer(plr, $"알 수 없는 명령어: {cmdName}");
                }
            }
            return false; // 명령은 브로드캐스트/릴레이에서 제외
        }
    }

    // 개인 채팅 회신 — AnnounceRelay.SendChatAnnouncementTo와 동일 포맷.
    internal static class ChatPrivateReply
    {
        internal static void SendToPlayer(NetPlayer plr, string message)
        {
            if (plr == null) return;
            AnnounceRelay.SendChatAnnouncementTo(message, plr.clientId);
        }
    }

    internal enum ChatMode
    {
        Global,
        Local,
    }

    // !chatmode — 채팅 공유 범위 토글 (Global/Local). 릴레이(ChatRelay)가 참조한다.
    internal static class ChatModeCommand
    {
        private const string CommandName = "chatmode";
        private static readonly Dictionary<knetid, ChatMode> ModeByClientId = new();

        internal static ChatMode GetMode(knetid clientId) =>
            ModeByClientId.TryGetValue(clientId, out ChatMode mode) ? mode : ChatMode.Global;

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            ChatMode current = GetMode(caller.clientId);
            ChatMode next = current == ChatMode.Global ? ChatMode.Local : ChatMode.Global;
            ModeByClientId[caller.clientId] = next;

            string label = next == ChatMode.Global
                ? "<color=#87CEEB>모든 레이어</color>"
                : "<color=#FFFF00>이 레이어</color>";
            ChatPrivateReply.SendToPlayer(caller, $"채팅 모드 토글 : {label}");
            return true;
        }
    }

    internal static class HelpCommand
    {
        private const string CommandName = "help";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            ChatPrivateReply.SendToPlayer(caller, "사용 가능한 명령어:");
            ChatPrivateReply.SendToPlayer(caller, "!help - 도움말 표시");
            ChatPrivateReply.SendToPlayer(caller, "!chatmode - 채팅 공유 범위 전환 (전체 레이어/현재 레이어)");
            ChatPrivateReply.SendToPlayer(caller, "!list - 접속 중인 플레이어 목록");
            ChatPrivateReply.SendToPlayer(caller, "!currentrun [key] - 현재 Run 설정 보기 (생략 시 전체 목록)");
            ChatPrivateReply.SendToPlayer(caller, "!calladmin - 관리자 호출");
            ChatPrivateReply.SendToPlayer(caller, "!discord - 디스코드 서버 초대 링크");
            ChatPrivateReply.SendToPlayer(caller, "!respawn - 사망 시 레이어 1에서 새 캐릭터로 리스폰");
            ChatPrivateReply.SendToPlayer(caller, "!runvote <설정> <값> - Run 설정 변경 투표 (전체 레이어)");
            ChatPrivateReply.SendToPlayer(caller, "!banvote <이름> - 플레이어 영구 차단 투표 (전체 레이어)");
            return true;
        }
    }

    internal static class PlayerListCommand
    {
        private const string CommandName = "list";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            OrchestratorClient.Instance?.SendEvent("LIST_REQUEST",
                new { playerKey = caller.GetPersistentId() });
            return true;
        }
    }

    internal static class CurrentRunCommand
    {
        private const string CommandName = "currentrun";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            string key = argv.Length > 1 ? argv[1] : "";
            OrchestratorClient.Instance?.SendEvent("CURRENT_REQUEST",
                new { playerKey = caller.GetPersistentId(), key });
            return true;
        }
    }

    internal static class CallAdminCommand
    {
        private const string CommandName = "calladmin";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            OrchestratorClient.Instance?.SendEvent("CALLADMIN",
                new { playerKey = caller.GetPersistentId(), username = caller.playername });
            ChatPrivateReply.SendToPlayer(caller, "관리자에게 호출이 전송되었습니다.");
            return true;
        }
    }

    // !discord — 디스코드 서버 초대 URL 표시. URL은 오케스트레이터가 단일 소유자.
    internal static class DiscordCommand
    {
        private const string CommandName = "discord";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            OrchestratorClient.Instance?.SendEvent("DISCORD_REQUEST",
                new { playerKey = caller.GetPersistentId() });
            return true;
        }
    }

    // !respawn — 완전 신규 리스폰 (사망 시에만). 레이어 1은 무로딩 인플레이스 리셋,
    // 레이어 N은 오케스트레이터 하향 마이그레이션. 세이브 폐기는 오케스트레이터 담당.
    internal static class RespawnCommand
    {
        private const string CommandName = "respawn";

        internal static bool TryHandle(NetPlayer caller, string[] argv)
        {
            if (argv.Length == 0 || argv[0] != CommandName || caller == null) return false;

            if (!caller.IsDead())
            {
                ChatPrivateReply.SendToPlayer(caller, "사망한 상태에서만 사용할 수 있는 명령어입니다.");
                return true;
            }

            string pid = caller.GetPersistentId();
            if (MigrationModule.IsFrozen(pid))
            {
                ChatPrivateReply.SendToPlayer(caller, "마이그레이션 중에는 리스폰할 수 없습니다.");
                return true;
            }

            if (OrchestratorClient.Instance == null)
            {
                ChatPrivateReply.SendToPlayer(caller, "오케스트레이터 연결이 없어 리스폰할 수 없습니다.");
                return true;
            }

            if (OrchestratorClient.Instance.InstanceDepth == 1)
            {
                RespawnLocally(caller);
            }
            else
            {
                OrchestratorClient.Instance.SendEvent("RESPAWN",
                    new { playerKey = pid, fromDepth = OrchestratorClient.Instance.InstanceDepth });
                ChatPrivateReply.SendToPlayer(caller, "리스폰 — 레이어 1로 이동합니다.");
            }
            return true;
        }

        // 무로딩 인플레이스 리스폰 (레이어 1).
        private static void RespawnLocally(NetPlayer caller)
        {
            string pid = caller.GetPersistentId();
            OrchestratorClient.Instance.SendEvent("RESPAWN", new { playerKey = pid, fromDepth = 1 });

            // 인벤토리/착용 파괴 (드랍 아님 — 유령 아이템 방지).
            // 주의: slots[i]=null 금지 — slots는 InventorySlot[] 컴포넌트 배열이라 참조를
            // 깨뜨리면 NRE가 난다. Object.Destroy는 프레임 말 반영 — 보급품 지급은 지연한다.
            // 컨테이너는 파괴 시 내용물을 드랍하므로 자식부터 파괴한다.
            try
            {
                var all = new List<Item>(caller.body.GetAllItemsThorough());
                all.Reverse(); // 자식 → 부모 순
                foreach (Item item in all)
                {
                    if (item == null) continue;
                    NetObjectRegistry.SafeDestroyObject(item.gameObject);
                }
                foreach (Item wearable in caller.body.GetAllWearables())
                {
                    if (wearable == null) continue;
                    NetObjectRegistry.SafeDestroyObject(wearable.gameObject);
                }
                // 슬롯 직결 아이템 안전망 (thorough가 놓친 항목 커버).
                foreach (InventorySlot slot in caller.body.slots)
                {
                    if (slot == null || slot.transform.childCount == 0) continue;
                    Item item = slot.transform.GetChild(0).GetComponent<Item>();
                    if (item != null) NetObjectRegistry.SafeDestroyObject(item.gameObject);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Respawn] {caller.playername} 인벤토리 파괴 실패: {ex.Message}");
            }

            try
            {
                // 게임 내장 무로딩 리스폰 경로 (레벨 전환 없음 — 드랍 경로 중첩 방지).
                caller.body.ResetMind(); // RespawnKeepSkills 규칙 무관 — 무조건 초기화
                caller.Server_RespawnCharacter(Body_PlaceBody_MultiplayerPatch.spawnlocation, level_transition: false);
                ChatPrivateReply.SendToPlayer(caller, "리스폰했습니다.");

                // 파괴가 끝나 슬롯이 비워진 뒤에 보급품을 지급한다.
                KrokoshaCasualtiesUtils.Util.DelayCallLambda(0.6f, (Action)(() =>
                {
                    if (caller == null || caller.body == null) return;
                    try
                    {
                        SaveModule.GrantStartingSupplies(caller.body, caller);
                    }
                    catch (System.Exception ex2)
                    {
                        Plugin.Log.LogWarning($"[Respawn] {caller.playername} 보급품 지급 실패: {ex2.Message}");
                    }
                }));
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Respawn] {caller.playername} 리스폰 실패: {ex.Message}");
                ChatPrivateReply.SendToPlayer(caller, "리스폰 처리에 실패했습니다. 관리자에게 문의하세요.");
            }
        }
    }

    // 결과 수신 (오케스트레이터 → 모드).

    internal static void HandleListResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<ListResultPayload>();
        if (payload == null || payload.Lines == null) return;
        NetPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        foreach (string line in payload.Lines)
        {
            ChatPrivateReply.SendToPlayer(plr, line);
        }
    }

    internal static void HandleCurrentResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<CurrentResultPayload>();
        if (payload == null) return;
        NetPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr != null) ChatPrivateReply.SendToPlayer(plr, payload.Text);
    }

    internal static void HandleDiscordResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<DiscordResultPayload>();
        if (payload == null) return;
        NetPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        if (string.IsNullOrEmpty(payload.Url))
        {
            ChatPrivateReply.SendToPlayer(plr, "디스코드 서버 URL이 설정되어 있지 않습니다.");
            return;
        }
        ChatPrivateReply.SendToPlayer(plr, "디스코드 서버 URL:");
        ChatPrivateReply.SendToPlayer(plr, payload.Url);
    }

    internal static NetPlayer FindByPersistentId(string persistentId) =>
        NetPlayer.ClientIdToPlayerDict.Values.FirstOrDefault(p => p.GetPersistentId() == persistentId);
}
