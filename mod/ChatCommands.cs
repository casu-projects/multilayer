using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

/// <summary>채팅 명령 시스템 — "!" 접두사 명령 라우터.
/// Chat.Server_PlayerChatMessageSend(10099 수신기)를 Prefix로 가로채 명령이면 원본
/// (브로드캐스트/릴레이 이벤트)을 스킵하고 레지스트리 등록 핸들러로 처리한다. 비명령 채팅은
/// reader 위치를 복원해 원본이 정상 동작하도록 한다. 크로스 인스턴스가 필요한 명령
/// (!list/!calladmin)은 오케스트레이터로 요청을 보내고 결과를 개인 회신한다.</summary>
public static class ChatCommands
{
    private const string CommandPrefix = "!";

    private static readonly ChatCommandRegistry _registry = new();

    static ChatCommands()
    {
        RegisterCommands();
    }

    /// <summary>내장 명령 등록 — !help는 레지스트리에서 자동 생성된다.</summary>
    private static void RegisterCommands()
    {
        _registry.Register("help", "도움말 표시", HelpCommand.Cmd);
        _registry.Register("chatmode", "채팅 공유 범위 전환", ChatModeCommand.Cmd);
        _registry.Register("list", "접속 중인 플레이어 목록", PlayerListCommand.Cmd);
        _registry.Register("calladmin", "관리자 호출", CallAdminCommand.Cmd);
        _registry.Register("discord", "디스코드 서버 초대 링크", DiscordCommand.Cmd);
        _registry.Register("respawn", "사망 시 레이어 1에서 새 캐릭터로 리스폰", RespawnCommand.Cmd);
        _registry.Register("banvote", "플레이어 영구 차단 투표", "<이름>", VoteCommands.CmdBanVote);
    }

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
                // 레지스트리 디스패치 — 명령 이름은 대소문자 무시.
                string cmdName = argv[0].ToLowerInvariant();
                if (_registry.TryGet(cmdName, out ChatCommand? registered))
                {
                    try
                    {
                        registered.Handler(plr, argv);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[ChatCmd] !{cmdName} 처리 오류: {ex.Message}");
                    }
                    return false;
                }

                // 게임 어드민 명령 dict 폴백 (대소문자 구분 — 바닐라 dict 동작 유지).
                var cmdDict = AccessTools.Field(typeof(ServerMain), "ServerClientCustomCommandsDict")
                    ?.GetValue(null) as Dictionary<string, Action<NetPlayer, string, string[]>>;
                if (cmdDict != null && cmdDict.ContainsKey(argv[0]))
                {
                    AccessTools.Method(typeof(ServerMain), "RunClientCustomCommand")
                        ?.Invoke(null, new object[] { command, plr });
                }
                else
                {
                    ChatPrivateReply.SendToPlayer(plr, $"알 수 없는 명령어: {argv[0]}");
                }
            }
            return false; // 명령은 브로드캐스트/릴레이에서 제외
        }
    }

    /// <summary>10098 패킷 type 2 (name="*", tag="") — 특정 플레이어에게만 보이는 개인 채팅.
    /// 표시: "[*]: 메시지" (ChatMsgContainer.Compile이 이름을 대괄호로 감쌈).
    /// AnnounceRelay.SendChatAnnouncementTo와 동일 포맷 — 위임 (2026-08-03 통일).</summary>
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

    /// <summary>!chatmode — 채팅 공유 범위 토글 (기본 Global, 마이그레이션 시 목적지 인스턴스에서
    /// 자연 리셋 — 인메모리 dict가 인스턴스별이므로). 릴레이(ChatRelay)가 참조한다.</summary>
    internal static class ChatModeCommand
    {
        private static readonly Dictionary<knetid, ChatMode> ModeByClientId = new();

        internal static ChatMode GetMode(knetid clientId) =>
            ModeByClientId.TryGetValue(clientId, out ChatMode mode) ? mode : ChatMode.Global;

        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            ChatMode current = GetMode(caller.clientId);
            ChatMode next = current == ChatMode.Global ? ChatMode.Local : ChatMode.Global;
            ModeByClientId[caller.clientId] = next;

            string label = next == ChatMode.Global
                ? "<color=#87CEEB>모든 레이어</color>"
                : "<color=#FFFF00>이 레이어</color>";
            ChatPrivateReply.SendToPlayer(caller, $"채팅 모드 토글 : {label}");
        }
    }

    /// <summary>!help — 레지스트리 기반 자동 생성 (이름순).</summary>
    internal static class HelpCommand
    {
        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            ChatPrivateReply.SendToPlayer(caller, "사용 가능한 명령어:");
            foreach (ChatCommand cmd in _registry.All.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                string usage = cmd.Usage.Length > 0 ? " " + cmd.Usage : "";
                ChatPrivateReply.SendToPlayer(caller, $"!{cmd.Name}{usage} - {cmd.Description}");
            }
        }
    }

    /// <summary>!list — 접속 중인 플레이어 목록 (오케스트레이터가 전 레이어 기준으로 회신).</summary>
    internal static class PlayerListCommand
    {
        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent("LIST_REQUEST",
                new { playerKey = caller.GetPersistentId() });
        }
    }

    /// <summary>!calladmin — 관리자 호출 (오케스트레이터가 Discord로 전파).</summary>
    internal static class CallAdminCommand
    {
        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent("CALLADMIN",
                new { playerKey = caller.GetPersistentId(), username = caller.playername });
            ChatPrivateReply.SendToPlayer(caller, "관리자에게 호출이 전송되었습니다.");
        }
    }

    /// <summary>!discord — 디스코드 서버 초대 URL 표시. URL은 오케스트레이터가 단일 소유자
    /// (orchestrator.json DiscordUrl) — 요청 시점에 회신받아 개인 채팅 2줄로 표시한다.</summary>
    internal static class DiscordCommand
    {
        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent("DISCORD_REQUEST",
                new { playerKey = caller.GetPersistentId() });
        }
    }

    /// <summary>!respawn — 완전 신규 리스폰 (바디/스킬/인벤토리 초기화 + 레이어 1 + 보급품).
    /// 사망 시에만 사용 가능. 레이어 1은 무로딩 인플레이스 리셋 (게임 내장 Server_RespawnCharacter
    /// 경로 — RegrowAllLimbs로 절단 포함 전 필드 초기화), 레이어 N은 오케스트레이터 하향
    /// 마이그레이션 (로딩 동반). 세이브 폐기/세션 프레시화는 오케스트레이터가 담당 (단일 소유자).</summary>
    internal static class RespawnCommand
    {
        internal static void Cmd(NetPlayer caller, string[] argv)
        {
            if (!caller.IsDead())
            {
                ChatPrivateReply.SendToPlayer(caller, "사망한 상태에서만 사용할 수 있는 명령어입니다.");
                return;
            }

            string pid = caller.GetPersistentId();
            if (MigrationModule.IsFrozen(pid))
            {
                ChatPrivateReply.SendToPlayer(caller, "마이그레이션 중에는 리스폰할 수 없습니다.");
                return;
            }

            if (OrchestratorClient.Instance == null)
            {
                ChatPrivateReply.SendToPlayer(caller, "오케스트레이터 연결이 없어 리스폰할 수 없습니다.");
                return;
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
        }

        /// <summary>Case B — 무로딩 인플레이스 리스폰. 세이브 폐기/세션 프레시화는 RESPAWN
        /// 이벤트로 오케스트레이터에 비동기 전달 (로컬 리셋과 독립 — 순서 무관).</summary>
        private static void RespawnLocally(NetPlayer caller)
        {
            string pid = caller.GetPersistentId();
            OrchestratorClient.Instance.SendEvent("RESPAWN", new { playerKey = pid, fromDepth = 1 });

            // 인벤토리/착용 파괴 (드랍 아님 — FREEZE와 동일 정책, 유령 아이템 원천 차단).
            // 주의 1: slots[i]=null 금지 — slots는 InventorySlot[] (컴포넌트 배열)이라 참조를
            // 깨뜨리면 살아있는 바디의 HoldingItem/PickUpItem 등에서 NRE가 발생한다.
            // 주의 2: Object.Destroy는 프레임 말에 실제 반영 — 보급품 지급은 그 이후로 지연한다
            // (아래 DelayCallLambda — 구 RespawnCommand의 ScheduleDelayedRespawn과 동일 원리).
            // 주의 3: 컨테이너는 파괴 시 내용물을 월드에 드랍(unload)하므로 자식부터 파괴한다.
            try
            {
                var all = new List<Item>(caller.body.GetAllItemsThorough());
                all.Reverse(); // 컨테이너 내용물(자식) → 컨테이너(부모) 순으로 파괴
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
                // 슬롯 직결 아이템 안전망 — 게임과 동일한 접근(HoldingItem: GetChild(0))으로
                // 파괴 (thorough가 놓친 항목 커버).
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
                // 게임 내장 무로딩 리스폰 경로: CreateCharacter(바디 재사용) → ResetHealth
                // (RegrowAllLimbs — 절단 복원 + 힌지 재연결, 림브/바디 전 필드 100/0)
                // → StopPiggyback → ForceStand → Server_TeleportCharacter(spawnlocation).
                // level_transition:false — 드랍 경로 없음 (위에서 이미 파괴 — 중첩 안전).
                caller.body.ResetMind(); // RespawnKeepSkills 규칙 무관 — 무조건 초기화
                caller.Server_RespawnCharacter(Body_PlaceBody_MultiplayerPatch.spawnlocation, level_transition: false);
                ChatPrivateReply.SendToPlayer(caller, "리스폰했습니다.");

                // Object.Destroy는 프레임 말에 실제 반영 — 파괴가 끝나 슬롯이 비워진 뒤에
                // 보급품을 지급한다 (같은 프레임 지급 시 옛 아이템 때문에 바닥에 드랍됨).
                KrokoshaCasualtiesUtils.Util.DelayCallLambda(0.6f, (Action)(() =>
                {
                    if (caller == null || caller.body == null) return;
                    try
                    {
                        SaveModule.GrantStartingSupplies(caller.body, caller); // startingsupplies 런 규칙
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

    // ── 결과 수신 (오케스트레이터 → 모드) ──

    internal static void HandleListResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<ListResultPayload>();
        if (payload == null || payload.Lines == null) return;
        NetPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        // 레이어당 한 줄씩 별도의 개인 채팅으로 표시 ("[*] [L1]: player1111" 형태).
        foreach (string line in payload.Lines)
        {
            ChatPrivateReply.SendToPlayer(plr, line);
        }
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
