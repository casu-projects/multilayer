using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod;

// 채팅 명령 시스템 - "!" 접두사 라우터. Chat.Server_PlayerChatMessageSend(10099)를 Prefix로
// 가로채 명령이면 레지스트리 핸들러로 처리하고 브로드캐스트를 스킵한다 (비명령 채팅은 reader
// 위치 복원으로 원본 동작). 크로스 인스턴스 명령(!list/!calladmin)은 오케스트레이터 경유
public static class ChatCommands
{
    private const string CommandPrefix = "!";

    private static readonly ChatCommandRegistry _registry = new();

    static ChatCommands()
    {
        RegisterCommands();
    }

    // 내장 명령 등록 - !help는 레지스트리에서 자동 생성된다
    private static void RegisterCommands()
    {
        _registry.Register("help", "Show help", HelpCommand.Cmd);
        _registry.Register("chatmode", "Toggle chat share scope", ChatModeCommand.Cmd);
        _registry.Register("list", "List online players", PlayerListCommand.Cmd);
        _registry.Register("calladmin", "Call an admin", CallAdminCommand.Cmd);
        _registry.Register("respawn", "Respawn on layer 1 as a new character (when dead)", RespawnCommand.Cmd);
        _registry.Register("pos", "Show current coordinates", PosCommand.Cmd);
        _registry.Register("banvote", "Vote to permanently ban a player", "<name>", VoteCommands.CmdBanVote);
    }

    [HarmonyPatch(typeof(Chat), "Server_PlayerChatMessageSend")]
    internal static class Chat_CommandRouterPatch
    {
        private static bool Prefix(knetid clientId, ref NetDataReader reader)
        {

            int startPosition = reader.Position;
            // 신버전 와이어: [bool team][bool whisper][knetid whisperTarget][string message]
            reader.Get(out bool _team);
            reader.Get(out bool _whisper);
            reader.Get(out knetid _whisperTarget);
            reader.Get(out string message);

            if (string.IsNullOrEmpty(message) || !message.StartsWith(CommandPrefix))
            {
                reader.SetPosition(startPosition); // 원본이 정상 읽도록 복원
                return true;
            }

            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr))
            {
                return false;
            }

            string command = message.Substring(CommandPrefix.Length);
            string[] argv = command.Trim().Split(' ');
            if (argv.Length > 0)
            {
                // 레지스트리 디스패치 - 명령 이름은 대소문자 무시
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

                // 게임 내장 커스텀 명령 dict 는 신버전 기반 모드에서 제거됨
                // (Multiplayer.RegisterCustomServerReceiver 기반). 미등록 명령은 개인 회신만 한다.
                ChatPrivateReply.SendToPlayer(plr, $"Unknown command: {argv[0]}");
            }
            return false; // 명령은 브로드캐스트/릴레이에서 제외
        }
    }

    // 10098 패킷 type 2 (name="*", tag="") - 특정 플레이어에게만 보이는 개인 채팅
    // 표시: "[*]: 메시지" (ChatMsgContainer.Compile이 이름을 대괄호로 감쌈)
    // AnnounceRelay.SendChatAnnouncementTo와 동일 포맷 - 위임 ( 통일)
    internal static class ChatPrivateReply
    {
        internal static void SendToPlayer(ScavPlayer plr, string message)
        {
            if (plr == null) return;
            AnnounceRelay.SendChatAnnouncementTo(message, plr.playerId);
        }
    }

    internal enum ChatMode
    {
        Global,
        Local,
    }

    // !chatmode - 채팅 공유 범위 토글 (기본 Global, 마이그레이션 시 목적지 인스턴스에서
    // 자연 리셋 - 인메모리 dict가 인스턴스별이므로). 릴레이(ChatRelay)가 참조한다
    internal static class ChatModeCommand
    {
        private static readonly Dictionary<knetid, ChatMode> ModeByClientId = new();

        internal static ChatMode GetMode(knetid clientId) =>
            ModeByClientId.TryGetValue(clientId, out ChatMode mode) ? mode : ChatMode.Global;

        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            ChatMode current = GetMode(caller.playerId);
            ChatMode next = current == ChatMode.Global ? ChatMode.Local : ChatMode.Global;
            ModeByClientId[caller.playerId] = next;

            string label = next == ChatMode.Global
                ? "<color=#87CEEB>all layers</color>"
                : "<color=#FFFF00>this layer</color>";
            ChatPrivateReply.SendToPlayer(caller, $"Chat mode toggled: {label}");
        }
    }

    // !help - 레지스트리 기반 자동 생성 (이름순)
    internal static class HelpCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            ChatPrivateReply.SendToPlayer(caller, "Available commands:");
            foreach (ChatCommand cmd in _registry.All.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                string usage = cmd.Usage.Length > 0 ? " " + cmd.Usage : "";
                ChatPrivateReply.SendToPlayer(caller, $"!{cmd.Name}{usage} - {cmd.Description}");
            }
        }
    }

    // !list - 접속 중인 플레이어 목록 (오케스트레이터가 전 레이어 기준으로 회신)
    internal static class PlayerListCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent("LIST_REQUEST",
                new { playerKey = MigrationModule.PlayerKeyOf(caller) });
        }
    }

    // !calladmin - 관리자 호출 (오케스트레이터가 Discord로 전파)
    internal static class CallAdminCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent("CALLADMIN",
                new { playerKey = MigrationModule.PlayerKeyOf(caller), username = caller.playerName });
            ChatPrivateReply.SendToPlayer(caller, "Your admin call has been sent.");
        }
    }

    // !respawn - 완전 신규 리스폰 (바디/스킬/인벤토리 초기화 + 레이어 1 + 보급품), 사망 시 전용
    // 레이어 1은 무로딩 인플레이스 리셋, 레이어 N은 오케스트레이터 하향 마이그레이션 (로딩 동반)
    // 세이브 폐기/세션 프레시화는 오케스트레이터가 담당
    internal static class RespawnCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            if (!caller.IsDead())
            {
                ChatPrivateReply.SendToPlayer(caller, "You can only use this while dead.");
                return;
            }

            string pid = MigrationModule.PlayerKeyOf(caller);
            if (MigrationModule.IsFrozen(pid))
            {
                ChatPrivateReply.SendToPlayer(caller, "You cannot respawn during migration.");
                return;
            }

            if (OrchestratorClient.Instance == null)
            {
                ChatPrivateReply.SendToPlayer(caller, "Cannot respawn: no orchestrator connection.");
                return;
            }

            if (OrchestratorClient.Instance.InstanceDepth == 1)
            {
                RespawnLocally(caller);
            }
            else
            {
                RespawnThenMigrate(caller, pid);
            }
        }

        // 레이어 N: 사망 상태로 바로 마이그레이션하면 클라이언트가 사망/관전 UI 인 채로
        // 월드를 재로드해 EventSystem NRE(UIUtil.GetEventSystemRaycastResults)가 발생한다(실측).
        // 먼저 인플레이스 리스폰으로 살아난 뒤 마이그레이션해 정상 UI 경로를 타게 한다.
        private static void RespawnThenMigrate(ScavPlayer caller, string pid)
        {
            int depth = OrchestratorClient.Instance.InstanceDepth;
            if (!PerformInPlaceRespawn(caller, grantSupplies: false))
            {
                // 인플레이스 리스폰 실패 - 기존 동작(사망 상태 마이그레이션)으로 폴백
                OrchestratorClient.Instance.SendEvent("RESPAWN",
                    new { playerKey = pid, fromDepth = depth });
                ChatPrivateReply.SendToPlayer(caller, "Respawning — moving to layer 1.");
                return;
            }

            ChatPrivateReply.SendToPlayer(caller, "Respawning — moving to layer 1.");

            // 클라가 revive 상태를 적용할 시간을 준 뒤 마이그레이션을 시작한다
            CasualtiesTogetherUtils.Util.DelayCallLambda(0.6f, (Action)(() =>
            {
                if (caller == null || caller.body == null) return;
                OrchestratorClient.Instance?.SendEvent("RESPAWN",
                    new { playerKey = pid, fromDepth = depth });
            }));
        }

        // Case B - 무로딩 인플레이스 리스폰. 세이브 폐기/세션 프레시화는 RESPAWN
        // 이벤트로 오케스트레이터에 비동기 전달 (로컬 리셋과 독립 - 순서 무관)
        private static void RespawnLocally(ScavPlayer caller)
        {
            string pid = MigrationModule.PlayerKeyOf(caller);
            OrchestratorClient.Instance.SendEvent("RESPAWN", new { playerKey = pid, fromDepth = 1 });
            if (PerformInPlaceRespawn(caller, grantSupplies: true))
            {
                ChatPrivateReply.SendToPlayer(caller, "You have respawned.");
            }
        }

        // 인벤토리/착용 파괴 + 게임 내장 무로딩 리스폰(바디 재사용). 성공 시 true.
        // grantSupplies=false: 곧바로 마이그레이션하는 경로(보급품은 목적지가 지급).
        private static bool PerformInPlaceRespawn(ScavPlayer caller, bool grantSupplies)
        {
            if (caller == null || caller.body == null) return false;

            // 인벤토리/착용 파괴 (드랍 아님 - FREEZE와 동일 정책, 유령 아이템 원천 차단)
            // 주의 1: slots[i]=null 금지 - slots는 InventorySlot[] (컴포넌트 배열)이라 참조를
            // 깨뜨리면 살아있는 바디의 HoldingItem/PickUpItem 등에서 NRE가 발생한다
            // 주의 2: Object.Destroy는 프레임 말에 실제 반영 - 보급품 지급은 그 이후로 지연한다
            // (아래 DelayCallLambda - 구 RespawnCommand의 ScheduleDelayedRespawn과 동일 원리)
            // 주의 3: 컨테이너는 파괴 시 내용물을 월드에 드랍(unload)하므로 자식부터 파괴한다
            try
            {
                var all = new List<Item>(caller.body.GetAllItemsThorough());
                all.Reverse(); // 컨테이너 내용물(자식) -> 컨테이너(부모) 순으로 파괴
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
                // 슬롯 직결 아이템 안전망 - 게임과 동일한 접근(HoldingItem: GetChild(0))으로
                // 파괴 (thorough가 놓친 항목 커버)
                foreach (InventorySlot slot in caller.body.slots)
                {
                    if (slot == null || slot.transform.childCount == 0) continue;
                    Item item = slot.transform.GetChild(0).GetComponent<Item>();
                    if (item != null) NetObjectRegistry.SafeDestroyObject(item.gameObject);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Respawn] {caller.playerName} 인벤토리 파괴 실패: {ex.Message}");
            }

            try
            {
                // 게임 내장 무로딩 리스폰 경로: CreateCharacter(바디 재사용) -> ResetHealth
                // (RegrowAllLimbs - 절단 복원 + 힌지 재연결, 림브/바디 전 필드 100/0)
                // > StopPiggyback -> ForceStand -> Server_TeleportCharacter(spawnlocation)
                // level_transition:false - 드랍 경로 없음 (위에서 이미 파괴 - 중첩 안전)
                caller.body.ResetMind(); // RespawnKeepSkills 규칙 무관 - 무조건 초기화
                caller.Server_RespawnCharacter(Body_PlaceBody_MultiplayerPatch.SpawnLocation, level_transition: false);

                if (grantSupplies)
                {
                    // Object.Destroy는 프레임 말에 실제 반영 - 파괴가 끝나 슬롯이 비워진 뒤에
                    // 보급품을 지급한다 (같은 프레임 지급 시 옛 아이템 때문에 바닥에 드랍됨)
                    CasualtiesTogetherUtils.Util.DelayCallLambda(0.6f, (Action)(() =>
                    {
                        if (caller == null || caller.body == null) return;
                        try
                        {
                            SaveModule.GrantStartingSupplies(caller.body, caller); // startingsupplies 런 규칙
                        }
                        catch (System.Exception ex2)
                        {
                            Plugin.Log.LogWarning($"[Respawn] {caller.playerName} 보급품 지급 실패: {ex2.Message}");
                        }
                    }));
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Respawn] {caller.playerName} 리스폰 실패: {ex.Message}");
                ChatPrivateReply.SendToPlayer(caller, "Respawn failed. Please contact an admin.");
                return false;
            }
        }
    }

    // !pos - 실행한 유저의 현재 블록 좌표 (본인에게만 개인 회신)
    internal static class PosCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            if (caller?.body == null) return;
            Vector2Int pos = WorldGeneration.world.WorldToBlockPos(caller.body.transform.position);
            ChatPrivateReply.SendToPlayer(caller, $"X: {pos.x} / Y: {pos.y}");
        }
    }

    // 결과 수신 (오케스트레이터 -> 모드)

    internal static void HandleListResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<ListResultPayload>();
        if (payload == null || payload.Lines == null) return;
        ScavPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        // 레이어당 한 줄씩 별도의 개인 채팅으로 표시 ("[*] [L1]: player1111" 형태)
        foreach (string line in payload.Lines)
        {
            ChatPrivateReply.SendToPlayer(plr, line);
        }
    }

    internal static ScavPlayer FindByPersistentId(string persistentId) =>
        ScavPlayer.ClientIdToPlayerDict.Values.FirstOrDefault(p => MigrationModule.PlayerKeyOf(p) == persistentId || p.GetPersistentId() == persistentId);
}
