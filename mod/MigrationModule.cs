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

// 오케스트레이터가 트랜잭션을 주도하고, 모드는 이벤트 보고/지시 수행만 한다
// 신버전 기반 모드는 LiteEntitySystem(ECS) 기반으로 바디/아이템 동기화를 자체 관리하므로
// 구 CoolSync 레지스트리 버스트(10170/10174) 기반 정리 로직은 제거되었다
public sealed class MigrationModule : MonoBehaviour
{
    private readonly HashSet<string> _reported = new();
    // 동결 플레이어 -> 현재 epoch (구 epoch 메시지는 무시 - 멱등)
    private static readonly ConcurrentDictionary<string, int> FrozenPlayers = new();
    // FREEZE 시점에 캡처한 playerId - PRECLEAR 시점의 재조회 실패(플레이어가 이미 사라짐 등)를
    // 방지한다. SWAP 전까지 동일 인스턴스에서 playerId 는 불변이므로 안전하게 재사용한다.
    private static readonly ConcurrentDictionary<string, int> FrozenPlayerIds = new();

    // 월드 생성 완료 통지는 실제 로컬 몸 준비보다 약 2.7~2.9초 빠르다. 4초까지 대기
    // (NO LOCAL NETBODY 스팸 방지)
    private const float ReleaseGraceSeconds = 4.0f;

    public static bool IsFrozen(string persistentId) => FrozenPlayers.ContainsKey(persistentId);

    // 목적지 접속 후 RESUME 도착 전 짧은 구간도 동기화를 막는다
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
            bool atEnd = p.ServerPlayerState != null
                && p.ServerPlayerState.finished_worldgen
                && p.body != null && p.body.alive && p.body.conscious
                && p.IsAtTheEndOfLayer();

            if (atEnd && !_reported.Contains(pid))
            {
                _reported.Add(pid);
                // 10016(로딩 트리거)은 FREEZE 처리에서 단일 발신한다. 여기서 먼저 보내면
                // FREEZE의 10016과 중복되어 클라이언트 재생성이 취소·재시작되고 상태가 손상된다
                int fromDepth = WorldGeneration.world.biomeDepth + 1;
                int maxLayers = WorldGeneration.world.amountOfLayers;
                OrchestratorClient.Instance.SendEvent("LAYER_END", new
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

    // 명령 처리 (오케스트레이터 -> 모드)

    // 리스폰 트랜잭션 여부 - 죽은 상태 캡처(SubmitPlayer)를 금지하고 목적지에서 프레시 신규를 보장한다
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
            if (Plugin.VerboseLogging) Plugin.Log.LogWarning($"[Migration] FREEZE: {playerKey} 바디 없음.");
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
            return;
        }

        // 리스폰 트랜잭션인데 아직 사망 상태면 인플레이스로 살려둔다 - 사망/관전 UI 인 채로
        // 목적지 월드를 재로드하면 클라 EventSystem NRE(UIUtil.GetEventSystemRaycastResults)가
        // 발생한다(실측). 명령 경로를 우회한 리스폰에 대한 안전망.
        if (respawn && !plr.body.alive)
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
            SaveModule.SubmitPlayer(plr);   // 직렬화 + 제출 (인메모리 핸드오프) - 인벤토리 캡처
        }

        // 인벤토리/착용 아이템은 월드에 드랍하지 않고 파괴한다 (드랍은 다른 유저가 주워
        // 복사할 수 있음 - 여기서는 서버 상태 정리)
        try
        {
            // GetAllItemsThorough는 슬롯/착용/컨테이너 내용물 포함 - 자식부터 해제되도록 역순 처리
            var items = new List<Item>(plr.body.GetAllItemsThorough());
            items.Reverse();

            // 같은 아이템이 두 번 잡혀도 삭제 예약은 한 번만
            var destroyed = new HashSet<Item>();
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

        // "레이어 끝에 닿자마자 바디가 사라지는" UX : 다른 클라이언트에 퇴장을 즉시 반영한다.
        // 신버전 기반 모드는 LES 가 바디 엔티티 수명주기를 관리하므로 raw 10170 브로드캐스트는
        // 제거되었다 (베이스가 실제 연결 해제/엔티티 제거 시 처리).
        // 발신 중단은 IsFrozen 상태 기반 게이트가 담당한다

        // 1초 후: FREEZE_DONE - SWAP 전 버스트/파괴가 정리될 시간을 준다.
        // 로딩 트리거(10016)는 목적지 인스턴스가 도착 시 단일 발신한다 (SendRegenerateWorld 는
        // BasePlayer_ServerCreatePlayer 도착 훅 -> OnConnectionRequest Postfix 에서 호출된다).
        // 출발지 연결은 곧 SWAP 에서 끊기므로 여기서 보내면 패킷이 유실되어 클라이언트가
        // 옛 월드에 남고, 목적지 조기 baseline 으로 크래시한다.
        Util.DelayCallLambda(1f, (Action)(() =>
        {
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
        }));
    }

    // SWAP 직전 pre-clear (오케스트레이터가 목적지 READY 확인 후 발신). 출발지에는 아직
    // 클라이언트의 스테일 EntitiesDict 가 그대로 남아 있으므로(SWAP 전 연결 유지), 이 시점에
    // 대상 클라이언트 전용 "전체 엔티티 파괴" RPC 를 enqueue 한다. 이후 ~1.5초 재전송으로
    // 도달을 보장한 뒤 PRECLEAR_DONE 을 알리고 곧바로 SWAP 이 일어난다. PlayerResyncTimeout(4s)
    // 이전에 스왑되므로 출발지가 재-baseline 으로 되살릴 틈이 없다. 이렇게 비워진 사전에
    // 목적지 첫 baseline 이 적용되어 ExecuteRpcs IndexOutOfRange 크래시가 사라진다.
    internal static void HandlePreClear(ControlMessage msg)
    {
        PlayerKeyPayload payload = msg.PayloadAs<PlayerKeyPayload>();
        string playerKey = payload?.PlayerKey ?? "";
        int epoch = payload?.Epoch ?? -1;

        // FREEZE 시점에 캡처한 playerId 를 우선 사용한다. PRECLEAR 시점에는 FREEZE 이후
        // 상태 변화로 재조회가 실패할 수 있어(조용히 사전을 못 비우는 원인) 캡처값을 신뢰한다.
        int playerId;
        if (FrozenPlayerIds.TryGetValue(playerKey, out int captured))
        {
            playerId = captured;
        }
        else
        {
            ScavPlayer found = FindByPersistentId(playerKey);
            if (found == null)
            {
                OrchestratorClient.Instance?.SendEvent("PRECLEAR_DONE", new { playerKey, epoch });
                return;
            }
            playerId = found.playerId;
        }

        FirePreClear(playerId);
        // pre-clear destroy RPC 는 비신뢰(unreliable) 틱 패킷으로 전송되므로, 1차가 SWAP 전에
        // 모두 도달하지 못할 수 있다(실측: 1차만 보내면 목적지 baseline 이 부분 적용되어
        // 'Bad data' + 플레이어 바디 미연결로 스폰 정지). 0.7초 뒤 2차 버스트로 도달을 보장한다.
        Util.DelayCallLambda(0.7f, (Action)(() => FirePreClear(playerId)));

        Util.DelayCallLambda(1.5f, (Action)(() =>
        {
            OrchestratorClient.Instance?.SendEvent("PRECLEAR_DONE", new { playerKey, epoch });
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

        // pre-swap 중단: 클라이언트는 출발지 월드에 그대로 있고(목적지 도착 전이므로 10016은
        // 아직 발신되지 않았다) 재생성 트리거를 보내지 않는다. FREEZE에서 파괴된 인벤토리만
        // 캡처 데이터로 복원한다 (복원은 목적지가 아니라 여기서).
        ScavPlayer plr = FindByPersistentId(playerKey);
        if (plr != null)
        {
            JToken payload = msg.Inner("payload");
            if (payload != null && payload.Type == JTokenType.Object && plr.body != null)
            {
                try
                {
                    SaveModule.RestorePlayer(plr.body, plr, (JObject)payload);
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
            OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
            return;
        }
        // 리스폰은 payload가 의도적으로 없다 (프레시 신규) - 형식 오류로 보지 않는다
        if (!respawn && (payload == null || payload.Type != JTokenType.Object))
        {
            Plugin.Log.LogWarning($"[Migration] RESUME 형식 오류: {playerKey}");
            OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
            return;
        }
        if (respawn)
        {
            // 리스폰: 데이터 보관 없음 - 목적지 Body.Start가 프레시 생성 + 시작 보급품 지급
            SaveModule.RemovePending(playerKey);
        }
        else
        {
            // 데이터 보관 -> 바디 생성 시(Body.Start) 복원 경로 사용
            SaveModule.SetPending(playerKey, (JObject)payload);
        }
        // 이 인스턴스에서도 동결 - 10168(WORLDGEN_DONE) 보고와 퇴장/저장 스킵에 필요
        // (FREEZE는 구 인스턴스에만 도착하므로 RESUME 시점에 설정. 중복 RESUME은 멱등)
        FrozenPlayers[playerKey] = epoch;

        // 로딩 트리거는 구 인스턴스의 FREEZE(1초 플러시 후 10016)가 단일 발신원이다
        // 여기서 재전송하면 대기 중인 클라이언트의 재생성이 취소·재시작되어 상태가 손상된다

        OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
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

        // RELEASE 직후 바로 동기화를 재개하면 아직 몸이 없는 클라이언트가 패킷을 받아
        // 터진다. 실제 준비까지 최대 약 2.9초가 걸렸으니 4초 여유를 둔다 (epoch 변경 시 취소)
        Util.DelayCallLambda(ReleaseGraceSeconds, (Action)(() =>
        {
            if (!FrozenPlayers.TryGetValue(playerKey, out int delayedCurrent)) return;
            if (epoch >= 0 && delayedCurrent != epoch) return;

            FrozenPlayers.TryRemove(playerKey, out _);
            FrozenPlayerIds.TryRemove(playerKey, out _);

            // 도착 직후 동기화를 막던 상태도 함께 해제
            ScavPlayer released = FindByPersistentId(playerKey);
            if (released != null)
            {
                MigrationArrivalTracker.ClientIds.Remove(released.playerId);
            }
        }));
    }

    // 클라이언트에 CLIENT_Announce_RegenerateWorld(10016) 전송 - 즉시 로딩 화면 진입
    // 이후 목적지 인스턴스의 GenerateWorld_SeedAnnounce(10010)으로 대기가 해소된다
    // 마이그레이션 도착 시 목적지 인스턴스가 단일 발신원이다. 발신은 OnConnectionRequest
    // Postfix(ClientIdToPeerDict 등록 이후)에서 이뤄진다 - IdentityPatch 참고.
    internal static void SendRegenerateWorld(ScavPlayer plr)
    {
        if (plr == null) return;
        IEnumerable<knetid> targets = new List<knetid> { plr.playerId };
        var writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_Announce_RegenerateWorld);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.doPod ? (ushort)1 : (ushort)0);
        Net.Server_SendToClientsVeryReliable(in writer, in targets);
    }

    // 후킹

    // finished_worldgen 수신 시 동결 플레이어의 월드젠 완료를 보고한다
    [HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_FinishedWorldgen")]
    internal static class ServerReceiver_FinishedWorldgen_ReportPatch
    {
        private static void Postfix(knetid clientId)
        {
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) return;

            string pid = PlayerKeyOf(plr);
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("WORLDGEN_DONE", new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
        }
    }

    // 동결(FREEZE) 플레이어 퇴장 -> PLAYER_LEFT (오케스트레이터가 데이터 확정)
    // 신버전 기반 모드는 바디 생성을 ScavPlayer.DestroyCharacterIfNoLocal 이 담당하므로
    // 구 CharSync 서버측 정리/신뢰성 10174 DELETE 는 제거되었다
    [HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
    internal static class ScavPlayer_OnDestroy_ReportLeftPatch
    {
        private static void Prefix(ScavPlayer __instance)
        {
            if (__instance == null || __instance.IsLocal) return;
            string pid = PlayerKeyOf(__instance);
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("PLAYER_LEFT",
                    new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
            // 완전 퇴장(실제 끊김) 공지는 게이트웨이 SESSION_DISCONNECTED 기준으로
            // 오케스트레이터가 전 레이어에 발신한다 (여기서는 발신하지 않음).
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

    // 오케스트레이터/게이트웨이는 STEAM_<SteamId> 키를 사용한다. 반면 전용 서버는
    // Steam 트랜스포트가 아니므로(게이트웨이가 LiteNetLib 로 접속) Net.IsRunningSteam=false
    // 이고 GetPersistentId() 는 NAME_<hex> 를 돌려준다. 이 불일치로 FREEZE/PRECLEAR/
    // PLAYER_DATA/LAYER_END 가 모두 빗나갔다. 게이트웨이 tail 로 주입된 plr.SteamId 가
    // 있으면 STEAM_ 키로 정규화해 오케스트레이터 키와 일치시킨다.
    internal static string PlayerKeyOf(ScavPlayer plr)
    {
        if (plr == null) return "";
        if (plr.SteamId != 0) return "STEAM_" + plr.SteamId;
        return plr.GetPersistentId();
    }
}
