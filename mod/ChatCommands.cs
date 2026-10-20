using CasuMp.Protocol;
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

public static class ChatCommands
{
    private const string CommandPrefix = "!";

    private static readonly ChatCommandRegistry _registry = new();

    static ChatCommands()
    {
        RegisterCommands();
    }

    private static void RegisterCommands()
    {
        _registry.Register("help", "Show help", HelpCommand.Cmd);
        _registry.Register("chatmode", "Toggle chat share scope", ChatModeCommand.Cmd);
        _registry.Register("list", "List online players", PlayerListCommand.Cmd);
        _registry.Register("respawn", "Respawn on layer 1 as a new character (when dead)", RespawnCommand.Cmd);
        _registry.Register("pos", "Show current coordinates", PosCommand.Cmd);
    }

    [HarmonyPatch(typeof(Chat), "Server_PlayerChatMessageSend")]
    internal static class Chat_CommandRouterPatch
    {
        private static bool Prefix(knetid clientId, ref NetDataReader reader)
        {

            int startPosition = reader.Position;
            reader.Get(out bool _team);
            reader.Get(out bool _whisper);
            reader.Get(out knetid _whisperTarget);
            reader.Get(out string message);

            if (string.IsNullOrEmpty(message) || !message.StartsWith(CommandPrefix))
            {
                reader.SetPosition(startPosition); // 명령이 아니면 원본 읽기 복원
                return true;
            }

            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr))
            {
                return false;
            }

            string command = message.Substring(CommandPrefix.Length); // "!" 제거
            string[] argv = CommandLineTokenizer.Split(command);
            if (argv.Length > 0)
            {
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

                ChatPrivateReply.SendToPlayer(plr, $"Unknown command: {argv[0]}");
            }
            return false; // 명령은 브로드캐스트/릴레이에서 제외
        }
    }

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

    internal static class PlayerListCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            OrchestratorClient.Instance?.SendEvent(MessageType.LIST_REQUEST,
                new { playerKey = MigrationModule.PlayerKeyOf(caller) });
        }
    }

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

        private static void RespawnThenMigrate(ScavPlayer caller, string pid)
        {
            int depth = OrchestratorClient.Instance.InstanceDepth;
            if (!PerformInPlaceRespawn(caller, grantSupplies: false)) // 먼저 살린 뒤 마이그레이션
            {
                OrchestratorClient.Instance.SendEvent(MessageType.RESPAWN,
                    new { playerKey = pid, fromDepth = depth });
                ChatPrivateReply.SendToPlayer(caller, "Respawning — moving to layer 1.");
                return;
            }

            ChatPrivateReply.SendToPlayer(caller, "Respawning — moving to layer 1.");

            CasualtiesTogetherUtils.Util.DelayCallLambda(0.6f, (Action)(() => // 클라 revive 적용 후
            {
                if (caller == null || caller.body == null) return;
                OrchestratorClient.Instance?.SendEvent(MessageType.RESPAWN,
                    new { playerKey = pid, fromDepth = depth });
            }));
        }

        private static void RespawnLocally(ScavPlayer caller)
        {
            string pid = MigrationModule.PlayerKeyOf(caller);
            OrchestratorClient.Instance.SendEvent(MessageType.RESPAWN, new { playerKey = pid, fromDepth = 1 });
            if (PerformInPlaceRespawn(caller, grantSupplies: true))
            {
                ChatPrivateReply.SendToPlayer(caller, "You have respawned.");
            }
        }

        private static bool PerformInPlaceRespawn(ScavPlayer caller, bool grantSupplies)
        {
            if (caller == null || caller.body == null) return false;

            try
            {
                var all = new List<Item>(caller.body.GetAllItemsThorough()); // 인벤토리 파괴(드랍 없이)
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
                caller.body.ResetMind(); // RespawnKeepSkills 규칙 무관 - 무조건 초기화
                caller.Server_RespawnCharacter(Body_PlaceBody_MultiplayerPatch.SpawnLocation, level_transition: false);

                if (grantSupplies)
                {
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

    internal static class PosCommand
    {
        internal static void Cmd(ScavPlayer caller, string[] argv)
        {
            if (caller?.body == null) return;
            Vector2Int pos = WorldGeneration.world.WorldToBlockPos(caller.body.transform.position);
            ChatPrivateReply.SendToPlayer(caller, $"X: {pos.x} / Y: {pos.y}");
        }
    }

    internal static void HandleListResult(ControlMessage msg)
    {
        var payload = msg.PayloadAs<ListResultPayload>();
        if (payload == null || payload.Lines == null) return;
        ScavPlayer plr = FindByPersistentId(payload.PlayerKey);
        if (plr == null) return;
        foreach (string line in payload.Lines)
        {
            ChatPrivateReply.SendToPlayer(plr, line);
        }
    }

    internal static ScavPlayer FindByPersistentId(string persistentId) =>
        ScavPlayer.ClientIdToPlayerDict.Values.FirstOrDefault(p => MigrationModule.PlayerKeyOf(p) == persistentId || p.GetPersistentId() == persistentId);
}
