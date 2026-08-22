using CasuMod.Patch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CasuMod;

// 오케스트레이터가 트랜잭션을 주도하고, 모드는 이벤트 보고/지시 수행만 한다
public sealed class MigrationModule : MonoBehaviour
{
    private readonly HashSet<string> _reported = new();
    private static readonly HashSet<string> _loadingTriggered = new();   // 10016(조기 로딩) 발신 기록 - 중단 복구용
    // 동결 플레이어 -> 현재 epoch (구 epoch 메시지는 무시 - 멱등)
    private static readonly ConcurrentDictionary<string, int> FrozenPlayers = new();

    // 월드 생성 완료 통지는 실제 로컬 몸 준비보다 약 2.7~2.9초 빠르다. 4초까지 대기
    // (NO LOCAL NETBODY 스팸 방지)
    private const float ReleaseGraceSeconds = 4.0f;

    public static bool IsFrozen(string persistentId) => FrozenPlayers.ContainsKey(persistentId);

    // 목적지 접속 후 RESUME 도착 전 짧은 구간도 동기화를 막는다
    public static bool IsArrivalBlocked(NetPlayer plr) =>
        plr != null && MigrationArrivalTracker.ClientIds.Contains(plr.clientId);

    public static int FrozenEpoch(string persistentId) =>
        FrozenPlayers.TryGetValue(persistentId, out int epoch) ? epoch : -1;

    private void Update()
    {
        if (!KrokoshaCasualtiesUtils.Util.IsWorldGenerated()) return;
        if (OrchestratorClient.Instance == null) return;

        foreach (NetPlayer p in NetPlayer.ClientIdToPlayerDict.Values)
        {
            string pid = p.GetPersistentId();
            bool atEnd = p.server_plrstate != null
                && p.server_plrstate.finished_worldgen
                && p.IsAliveAndConscious()
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
        NetPlayer plr = FindByPersistentId(playerKey);
        if (plr == null || plr.body == null)
        {
            if (Plugin.VerboseLogging) Plugin.Log.LogWarning($"[Migration] FREEZE: {playerKey} 바디 없음.");
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
            return;
        }

        FrozenPlayers[playerKey] = epoch;
        if (!respawn)
        {
            SaveModule.SubmitPlayer(plr);   // 직렬화 + 제출 (인메모리 핸드오프) - 인벤토리 캡처
        }
        else
        {
        // 리스폰: 캡처 금지 - 목적지에서 프레시 신규 상태 보장
        }

        // 파괴 전 버스트 전송 - 아직 등록 상태여야 server_objects에 포함된다
        // ReliableOrdered 직접 전송이라 forcesync 큐(사망 플레이어 한도 20)에 의존하지 않고
        // 결정적으로 클라이언트에 삭제가 전달된다. 파괴 후 보내면 유령 아이템이 잔존한다
        SendRegistryCleanupBurst(plr);

        // 인벤토리/착용 아이템은 월드에 드랍하지 않고 파괴한다 (드랍은 다른 유저가 주워
        // 복사할 수 있음 - 여기서는 서버 상태 정리, 클라이언트는 위 버스트로 이미 삭제됨)
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

            // body.slots는 InventorySlot[] 컴포넌트 배열 - null로 비우지 않는다 (슬롯 기능 손상)
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] {playerKey} 인벤토리 파괴 실패: {ex.Message}");
        }

        // "레이어 끝에 닿자마자 바디가 사라지는" UX : 다른 클라이언트에 10170(퇴장)을
        // 즉시 브로드캐스트 - 바디는 SWAP 디스커넥트 전에 화면에서 제거된다. 반드시 본인
        // 제외 (본인이 수신하면 LOCAL_PLAYER가 파괴된다)
        SendPlayerLeftToOthers(plr);
        // 발신 중단은 QuiescencePatches 게이트가 담당 (IsFrozen - 전 스트림)

        // 1초 후: 10016(로딩 화면) + FREEZE_DONE - SWAP이 구 인스턴스 연결을 끊기 전에
        // 버스트/파괴가 정리될 시간 확보 (순서: 버스트->파괴->1초->10016->완료)
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(1f, (Action)(() =>
        {
            if (plr == null) return;
            SendRegenerateWorld(plr);
            _loadingTriggered.Add(playerKey);
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
        }));
    }

    // client_objects 레지스트리는 forcedelete(10174, result4==0)로만 제거된다 (월드 재생성으로
    // 정리 안 됨). 출발지 객체가 고아로 잔존하면 목적지의 낮은 netId 재사용이 스테일 항목에
    // 부딪혀 프리팹 오염(emergencylight 버그)/NRE가 발생한다 - 객체/CharSync/PlrSync 전
    // 등록 객체를 forcedelete로 일괄 비운다 (본인 바디는 재생성되므로 제외)
    // 서버 PackAndSend 와이어 형식 미러링: [systemid][deltaid][count][netId,0]… + CompressWriter
    private static void SendRegistryCleanupBurst(NetPlayer plr)
    {
        try
        {
            int sentPackets = 0;
            int sentObjects = 0;

            foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
            {
                byte systemId = sys.syncsystemid;
                if (systemId != 1 && systemId != 2 && systemId != 3) continue; // 객체/바디/플레이어만
                bool excludeSelf = systemId == 2 || systemId == 3; // CharSync/PlrSync - 본인 제외

                int sent = SendDeleteBurstForSystem(plr, sys, systemId, excludeSelf);
                sentPackets += Math.Max(0, sent >> 16);
                sentObjects += Math.Max(0, sent & 0xFFFF);
            }

            if (sentPackets > 0)
            {
            }
        }
        catch (System.Exception ex)
        {
        }
    }

    // 단일 시스템의 모든 등록 객체를 대상 플레이어에게 forcedelete로 일괄 전송
    // 반환값: (패킷 수 &lt;&lt; 16) | 객체 수
    private static int SendDeleteBurstForSystem(NetPlayer plr, BaseCoolSyncSubSystem sys, byte systemId, bool excludeSelf)
    {
        try
        {
            var serverObjects = AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_objects")
                ?.GetValue(sys) as System.Collections.IDictionary;
            if (serverObjects == null || serverObjects.Count == 0) return 0;

            var netIds = new List<ushort>(serverObjects.Count);
            foreach (System.Collections.DictionaryEntry entry in serverObjects)
            {
                ushort netId = (ushort)(knetid)entry.Key;
                if (excludeSelf && netId == plr.clientId) continue;
                netIds.Add(netId);
            }
            if (netIds.Count == 0) return 0;

            List<knetid> targets = new List<knetid> { plr.clientId };
            int sentPackets = 0;
            // 10174 형식 미러링: [systemid][deltaid][count][netId,0]… + 압축
            const int chunkSize = 120;
            for (int i = 0; i < netIds.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, netIds.Count - i);
                var writer = Net.CreateWriter(10174);
                writer.Put(systemId);
                int startToIgnore = writer.Length + 2;
                writer.Put((ushort)0); // deltaid - 삭제는 값 무시
                writer.Put((byte)count);
                for (int j = 0; j < count; j++)
                {
                    writer.Put(netIds[i + j]);
                    writer.Put((byte)0); // result4 == 0 -> 삭제
                }
                writer.CompressWriter(startToIgnore);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
                sentPackets++;
            }
            return (sentPackets << 16) | netIds.Count;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] 삭제 버스트 실패 (system {systemId}): {ex.Message}");
            return 0;
        }
    }

    // 10170(플레이어 퇴장)을 이 인스턴스의 다른 클라이언트 전체에 브로드캐스트
    // 대상에서 이주 플레이어 본인은 반드시 제외 (본인 수신 시 LOCAL_PLAYER 파괴 위험)
    private static void SendPlayerLeftToOthers(NetPlayer leaving)
    {
        try
        {
            List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
                .Where(id => id != leaving.clientId)
                .ToList();
            if (targets.Count == 0) return;
            SendPlayerLeft(leaving.clientId, targets);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] FREEZE 10170 브로드캐스트 실패: {ex.Message}");
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

        // 중단 복구: 10016(조기 로딩)을 보냈던 플레이어면 구 월드로 복귀시킨다
        // 클라이언트는 여전히 로딩 화면(파라미터 대기)이므로 10016 재전송 + 구 월드
        // 파라미터 announce로 재생성시킨다. (스왑 후 UNFREEZE는 플레이어가 이미 새
        // 백엔드에 연결되어 있어 이 인스턴스의 패킷이 도달하지 않으므로 무해)
        if (_loadingTriggered.Remove(playerKey))
        {
            NetPlayer plr = FindByPersistentId(playerKey);
            if (plr != null)
            {
                // FREEZE에서 파괴된 인벤토리/객체의 스테일 항목을 먼저 정리 후 구 월드
                // 재생성 (10016 이전에 삭제 버스트 - FREEZE 경로와 동일한 순서 원칙)
                try { SendRegistryCleanupBurst(plr); }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[Migration] UNFREEZE 레지스트리 정리 실패: {ex.Message}"); }
                SendRegenerateWorld(plr);
                try
                {
                    ServerMain.Server_AnnounceSeed(new List<knetid> { plr.clientId });
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Migration] UNFREEZE 복구 announce 실패: {ex.Message}");
                }

                // pre-swap 중단: 인벤토리는 FREEZE에서 이미 파괴됨 - 캡처 데이터로 땅 드랍 없이 복원한다
                // 위치는 RestorePlayer의 QueuePosition이 적재하며, 구 월드 재생성 후 LateSpawnLocation 시점에 적용된다
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

        // 이전 레이어의 잔존 NetPlayer를 도착 플레이어 클라이언트에서 즉시 제거하도록
        // 10170(퇴장 신호) 전송 (새 인스턴스는 그들을 모르므로 바닐라가 자연 정리 못함)
        NetPlayer arrival = FindByPersistentId(playerKey);
        if (arrival != null && msg.Inner("ghostClientIds") is JArray ghostArr)
        {
            int sent = 0;
            foreach (JValue v in ghostArr.OfType<JValue>())
            {
                if (v.Value == null) continue;
                try { SendPlayerLeft(Convert.ToUInt16(v.Value), new List<knetid> { arrival.clientId }); sent++; }
                catch { }
            }
            if (sent > 0)
            {
            }
        }
        // 로딩 트리거는 구 인스턴스의 FREEZE(1초 플러시 후 10016)가 단일 발신원이다
        // 여기서 재전송하면 대기 중인 클라이언트의 재생성이 취소·재시작되어 상태가 손상된다

        OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
    }

    internal static void HandleTriggerWorldgen(string playerKey)
    {
        NetPlayer plr = FindByPersistentId(playerKey);
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

        // RELEASE 직후 바로 동기화를 재개하면 아직 몸이 없는 클라이언트가 10174를 받아
        // 터진다. 실제 준비까지 최대 약 2.9초가 걸렸으니 4초 여유를 둔다 (epoch 변경 시 취소)
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(ReleaseGraceSeconds, (Action)(() =>
        {
            if (!FrozenPlayers.TryGetValue(playerKey, out int delayedCurrent)) return;
            if (epoch >= 0 && delayedCurrent != epoch) return;

            FrozenPlayers.TryRemove(playerKey, out _);
            _loadingTriggered.Remove(playerKey);

            // 도착 직후 동기화를 막던 상태도 함께 해제
            NetPlayer released = FindByPersistentId(playerKey);
            if (released != null)
            {
                MigrationArrivalTracker.ClientIds.Remove(released.clientId);
            }
        }));
    }

    // 클라이언트에 10170(RegenerateWorld) 전송 - 즉시 로딩 화면 진입
    // 이후 목적지 인스턴스의 10010(파라미터 announce)으로 대기가 해소된다
    private static void SendRegenerateWorld(NetPlayer plr)
    {
        if (plr == null) return;
        IEnumerable<knetid> targets = new List<knetid> { plr.clientId };
        var writer = Net.CreateWriter(10016);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.doPod ? (ushort)1 : (ushort)0);
        Net.Server_SendToClientsVeryReliable(in writer, in targets);
    }

    // 10170(플레이어 퇴장) - 클라이언트가 해당 clientId의 NetPlayer를 파괴한다
    // 와이어: [ushort clientId][ulong steam_id] (수신기가 steam_id를 읽지 않아 0 전송)
    private static void SendPlayerLeft(ushort leavingClientId, List<knetid> targets)
    {
        var writer = Net.CreateWriter(10170);
        writer.Put(leavingClientId);
        writer.Put(0UL);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    // 후킹

    // finished_worldgen 수신 시 신원(10023)을 타인에게 재브로드캐스트 - 수신자별
    // finished_worldgen 이후 발신이라 로딩 중 유실이 없어 "나중 마이그레이션 유저가
    // 안 보이는" 레이스가 해결된다
    [HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_FinishedWorldgen")]
    internal static class ServerReceiver_FinishedWorldgen_ReportPatch
    {
        private static void Postfix(knetid clientId)
        {
                if (!NetPlayer.TryGetPlayerFromClientId(clientId, out NetPlayer plr)) return;

            try { RosterBarrier(plr); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Roster] 배리어 실패: {ex.Message}"); }

            string pid = plr.GetPersistentId();
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("WORLDGEN_DONE", new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
        }
    }

    // 월드젠 완료 시 신원 교환을 결정적으로 완료 (나 -> 타인, 타인 -> 나)
    // 바디 CharSync DELETE는 제거: 살아있는 바디 항목을 삭제하면 같은 프레임 재동기화가
    // 파괴 지연 전의 기존 바디를 반환해 파괴된 바디에 Apply -> NRE + 바디 미표시 스턱 (실측)
    // 신원(10023)은 바디 CharSync와 별개 채널이라 삭제 없이도 관찰자 바디가 자연 동기화된다
    private static void RosterBarrier(NetPlayer plr)
    {
        List<knetid> others = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != plr.clientId)
            .ToList();

        RosterBroadcast.SendIdentity(plr, "WorldgenDone");

        foreach (NetPlayer other in NetPlayer.ClientIdToPlayerDict.Values)
        {
            if (other.clientId == plr.clientId) continue;
            try { other.Server__ResponsePlayerName(new List<knetid> { plr.clientId }); }
            catch { }
        }
    }

    // 동결(FREEZE) 플레이어 퇴장 -> PLAYER_LEFT (오케스트레이터가 데이터 확정)
    // : 퇴장자의 CharSync 바디를 서버측에서 제거 - 바닐라 OnDestroy는 PlrSync만 지우고
    // CharSync는 고아로 남아, 같은 clientId(같은 netId) 재접속/마이그레이션 시 파괴된
    // 바디에 Apply -> 10174 NRE 스팸의 근본 원인. Postfix에서 수행 - OnDestroy가 클라이언트
    // 목록 제거 후 실행되므로 DELETE가 본인에게 도달하지 않는다 (Prefix면 본인 바디가 파괴되어
    // PlayerCamera/Observer NRE 폭풍 + 로딩 화면 프리즈 - 실측)
    [HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.OnDestroy))]
    internal static class NetPlayer_OnDestroy_ReportLeftPatch
    {
        private static void Prefix(NetPlayer __instance)
        {
                if (__instance == null || __instance.is_local) return;
            string pid = __instance.GetPersistentId();
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("PLAYER_LEFT",
                    new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
            // 완전 퇴장(실제 끊김) 공지는 게이트웨이 SESSION_DISCONNECTED 기준으로
            // 오케스트레이터가 전 레이어에 발신한다 (여기서는 발신하지 않음).
        }

        private static void Postfix(NetPlayer __instance)
        {
                if (__instance == null || __instance.is_local) return;

            // CharSync 바디 고아화 방지: 서버측 객체 제거 + 신뢰성 raw 10174 DELETE를
            // 남은 클라이언트 전체에 즉시 전송. Server_DeleteObject의 비신뢰 CharSync 채널은
            // 재접속 재동기화와 레이스하므로 신뢰성 채널로 먼저 제거한다 (재접속은 수 초 후)
            try
            {
                foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
                {
                    if (sys.syncsystemid != 2) continue;
                    if (sys is CoolSyncSubSystemForObjects objSys)
                    {
                        objSys.Server_DeleteObject(__instance.clientId);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Migration] CharSync 바디 정리 실패: {ex.Message}");
            }

            try
            {
                SendReliableCharSyncDelete(__instance.clientId);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Migration] CharSync 항목 삭제 전송 실패: {ex.Message}");
            }

            ReturningTracker.ClientIds.Remove(__instance.clientId);
            MigrationArrivalTracker.ClientIds.Remove(__instance.clientId);
        }
    }

    // 퇴장자의 CharSync 항목을 남은 클라이언트 전체에서 제거 (10174 system 2, result4=0,
    // ReliableOrdered) - 같은 netId 재접속 재동기화의 NRE 스턱 방지. 본인은 연결 종료로 수신 불가
    private static void SendReliableCharSyncDelete(ushort leavingClientId)
    {
        List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != leavingClientId)
            .ToList();
        if (targets.Count == 0) return;

        var writer = Net.CreateWriter(10174);
        writer.Put((byte)2);          // CharSync
        int startToIgnore = writer.Length + 2;
        writer.Put((ushort)0);        // deltaid - 삭제는 값 무시
        writer.Put((byte)1);
        writer.Put(leavingClientId);
        writer.Put((byte)0);          // result4 == 0 -> 삭제
        writer.CompressWriter(startToIgnore);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    private static NetPlayer FindByPersistentId(string persistentId)
    {
        return NetPlayer.ClientIdToPlayerDict.Values
            .FirstOrDefault(p => p.GetPersistentId() == persistentId);
    }
}
