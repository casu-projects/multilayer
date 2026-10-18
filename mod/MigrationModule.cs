using CasuMp.Protocol;
using CasuMod.Patch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasuMod;

public sealed class MigrationModule : MonoBehaviour
{
    private readonly HashSet<string> _reported = new(); // LAYER_END 중복 보고 방지
    private static readonly ConcurrentDictionary<string, int> FrozenPlayers = new(); // persistentId -> epoch
    private static readonly ConcurrentDictionary<string, int> FrozenPlayerIds = new(); // FREEZE 시점 playerId 캡처

    private const float ReleaseGraceSeconds = 4.0f;

    public static bool IsFrozen(string persistentId) => FrozenPlayers.ContainsKey(persistentId);

    public static bool IsArrivalBlocked(ScavPlayer plr) =>
        plr != null && MigrationArrivalTracker.ClientIds.Contains(plr.playerId);

    public static int FrozenEpoch(string persistentId) =>
        FrozenPlayers.TryGetValue(persistentId, out int epoch) ? epoch : -1;

    private void Update()
    {
        if (!Util.IsWorldGenerated()) return;
        if (OrchestratorClient.Instance == null) return;
        if (WorldGeneration.world == null) return;

        foreach (ScavPlayer p in ScavPlayer.ClientIdToPlayerDict.Values)
        {
            string pid = PlayerKeyOf(p);
            bool atEnd = p.ServerPlayerState != null // 레이어 끝 도달 여부
                && p.ServerPlayerState.finished_worldgen
                && p.body != null && p.body.alive && p.body.conscious
                && p.IsAtTheEndOfLayer();

            if (atEnd && !_reported.Contains(pid))
            {
                _reported.Add(pid);
                int fromDepth = WorldGeneration.world.biomeDepth + 1;
                int maxLayers = WorldGeneration.world.amountOfLayers;
                OrchestratorClient.Instance.SendEvent(MessageTypes.LayerEnd, new // 마이그레이션 트랜잭션 시작
                {
                    playerKey = pid,
                    fromDepth,
                    maxLayers,
                });
            }
            else if (!atEnd && _reported.Contains(pid))
            {
                _reported.Remove(pid); // 끝에서 벗어나면 재보고 허용
            }
        }
    }

    private static bool IsRespawn(ControlMessage msg)
    {
        JToken flag = msg.Inner("respawn");
        return flag != null && flag.Type == JTokenType.Boolean && (bool)flag;
    }

    internal static void HandleFreeze(ControlMessage msg)
    {
        string playerKey = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "";
        int epoch = msg.PayloadAs<PlayerKeyPayload>()?.Epoch ?? -1;
        bool respawn = IsRespawn(msg);
        ScavPlayer plr = FindByPersistentId(playerKey);
        if (plr == null || plr.body == null)
        {
            OrchestratorClient.Instance?.SendEvent(MessageTypes.FreezeDone, new { playerKey, epoch });
            return;
        }

        if (respawn && !plr.body.alive) // 사망 상태로 월드 재로드 시 클라 UI NRE 방지
        {
            try
            {
                plr.body.ResetMind();
                plr.Server_RespawnCharacter(Body_PlaceBody_MultiplayerPatch.SpawnLocation, level_transition: false);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Migration] {playerKey} 리스폰 전 revive 실패: {ex.Message}");
            }
        }

        FrozenPlayers[playerKey] = epoch;
        FrozenPlayerIds[playerKey] = plr.playerId;
        if (!respawn)
        {
            SaveModule.SubmitPlayer(plr);   // 인벤토리/상태 캡처 (오케스트레이터 핸드오프)
        }

        try
        {
            var items = new List<Item>(plr.body.GetAllItemsThorough());
            items.Reverse();

            var destroyed = new HashSet<Item>(); // 드랍 없이 파괴(아이템 복사 방지)
            foreach (Item item in items)
            {
                if (item == null || !destroyed.Add(item)) continue;
                NetObjectRegistry.SafeDestroyObject(item.gameObject);
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] {playerKey} 인벤토리 파괴 실패: {ex.Message}");
        }

        Util.DelayCallLambda(1f, (Action)(() => // 파괴/정리 시간 확보 후 완료
        {
            OrchestratorClient.Instance?.SendEvent(MessageTypes.FreezeDone, new { playerKey, epoch });
        }));
    }

    internal static void HandlePreClear(ControlMessage msg)
    {
        PlayerKeyPayload payload = msg.PayloadAs<PlayerKeyPayload>();
        string playerKey = payload?.PlayerKey ?? "";
        int epoch = payload?.Epoch ?? -1;

        int playerId;
        if (FrozenPlayerIds.TryGetValue(playerKey, out int captured)) // FREEZE 캡처 id 우선
        {
            playerId = captured;
        }
        else
        {
            ScavPlayer found = FindByPersistentId(playerKey);
            if (found == null)
            {
                OrchestratorClient.Instance?.SendEvent(MessageTypes.PreClearDone, new { playerKey, epoch });
                return;
            }
            playerId = found.playerId;
        }

        FirePreClear(playerId); // 대상 클라 전용 엔티티 파괴 RPC
        Util.DelayCallLambda(0.7f, (Action)(() => FirePreClear(playerId))); // 패킷 유실 대비 2차

        Util.DelayCallLambda(1.5f, (Action)(() => // SWAP 전 완료 통지
        {
            OrchestratorClient.Instance?.SendEvent(MessageTypes.PreClearDone, new { playerKey, epoch });
        }));
    }

    private static void FirePreClear(int playerId)
    {
        try
        {
            MigrationPreClearPatch.Run((ushort)playerId);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] pre-clear 실패: {ex.Message}");
        }
    }

    internal static void HandleUnfreeze(ControlMessage msg)
    {
        string playerKey = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "";
        int epoch = msg.PayloadAs<PlayerKeyPayload>()?.Epoch ?? -1;
        if (!FrozenPlayers.TryGetValue(playerKey, out int current) || (epoch >= 0 && epoch != current))
        {
            return;
        }
        FrozenPlayers.TryRemove(playerKey, out _);
        FrozenPlayerIds.TryRemove(playerKey, out _);

        ScavPlayer plr = FindByPersistentId(playerKey);
        if (plr != null)
        {
            JToken payload = msg.Inner("payload");
            if (payload != null && payload.Type == JTokenType.Object && plr.body != null)
            {
                try
                {
                    SaveModule.RestorePlayer(plr.body, plr, (JObject)payload); // 중단 롤백: 캡처 데이터 복원
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Migration] {playerKey} UNFREEZE 데이터 재적용 실패: {ex.Message}");
                }
            }
        }
    }

    internal static void HandleResume(ControlMessage msg)
    {
        string playerKey = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey ?? "";
        int epoch = msg.PayloadAs<PlayerKeyPayload>()?.Epoch ?? -1;
        JToken payload = msg.Inner("payload");
        bool respawn = IsRespawn(msg);
        if (playerKey == "")
        {
            Plugin.Log.LogWarning($"[Migration] RESUME 형식 오류: {playerKey}");
            OrchestratorClient.Instance?.SendEvent(MessageTypes.ResumeDone, new { playerKey, epoch });
            return;
        }
        if (!respawn && (payload == null || payload.Type != JTokenType.Object))
        {
            Plugin.Log.LogWarning($"[Migration] RESUME 형식 오류: {playerKey}");
            OrchestratorClient.Instance?.SendEvent(MessageTypes.ResumeDone, new { playerKey, epoch });
            return;
        }
        if (respawn)
        {
            SaveModule.RemovePending(playerKey); // 리스폰: 데이터 없음 -> 프레시 생성
        }
        else
        {
            SaveModule.SetPending(playerKey, (JObject)payload); // 마이그레이션 데이터 보관
        }
        FrozenPlayers[playerKey] = epoch; // 도착지에서도 동결

        OrchestratorClient.Instance?.SendEvent(MessageTypes.ResumeDone, new { playerKey, epoch });
    }

    internal static void HandleTriggerWorldgen(string playerKey)
    {
        ScavPlayer plr = FindByPersistentId(playerKey);
        if (plr == null)
        {
            return;
        }

        SendRegenerateWorld(plr);
    }

    internal static void HandleRelease(string playerKey, int epoch)
    {
        if (!FrozenPlayers.TryGetValue(playerKey, out int current) || (epoch >= 0 && epoch != current))
        {
            return;
        }

        Util.DelayCallLambda(ReleaseGraceSeconds, (Action)(() => // 동기화 재개 유예
        {
            if (!FrozenPlayers.TryGetValue(playerKey, out int delayedCurrent)) return;
            if (epoch >= 0 && delayedCurrent != epoch) return;

            FrozenPlayers.TryRemove(playerKey, out _);
            FrozenPlayerIds.TryRemove(playerKey, out _);

            ScavPlayer released = FindByPersistentId(playerKey);
            if (released != null)
            {
                MigrationArrivalTracker.ClientIds.Remove(released.playerId);
            }
        }));
    }

    internal static void SendRegenerateWorld(ScavPlayer plr)
    {
        if (plr == null) return;
        IEnumerable<knetid> targets = new List<knetid> { plr.playerId };
        var writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_Announce_RegenerateWorld); // 10016 로딩 트리거
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.doPod ? (ushort)1 : (ushort)0);
        Net.Server_SendToClientsVeryReliable(in writer, in targets);
    }

    [HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_FinishedWorldgen")]
    internal static class ServerReceiver_FinishedWorldgen_ReportPatch
    {
        private static void Postfix(knetid clientId)
        {
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) return;

            string pid = PlayerKeyOf(plr);
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent(MessageTypes.WorldgenDone, new { playerKey = pid, epoch = FrozenEpoch(pid) }); // 월드젠 완료
            }
        }
    }

    [HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
    internal static class ScavPlayer_OnDestroy_ReportLeftPatch
    {
        private static void Prefix(ScavPlayer __instance)
        {
            if (__instance == null || __instance.IsLocal) return;
            string pid = PlayerKeyOf(__instance);
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent(MessageTypes.PlayerLeft, // 동결 중 퇴장 보고
                    new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
        }

        private static void Postfix(ScavPlayer __instance)
        {
            if (__instance == null || __instance.IsLocal) return;
            ReturningTracker.ClientIds.Remove(__instance.playerId);
            MigrationArrivalTracker.ClientIds.Remove(__instance.playerId);
            MigrationTickAlignPatch.Reset(__instance.playerId);
            FrozenPlayerIds.TryRemove(PlayerKeyOf(__instance), out _);
        }
    }

    private static ScavPlayer FindByPersistentId(string persistentId)
    {
        return ScavPlayer.ClientIdToPlayerDict.Values
            .FirstOrDefault(p => PlayerKeyOf(p) == persistentId || p.GetPersistentId() == persistentId);
    }

    internal static string PlayerKeyOf(ScavPlayer plr)
    {
        if (plr == null) return "";
        if (plr.SteamId != 0) return "STEAM_" + plr.SteamId; // 전용 서버는 NAME_hex -> STEAM_ 키로 정규화
        return plr.GetPersistentId();
    }
}
