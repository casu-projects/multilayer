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

// 레이어 마이그레이션 실행 — 오케스트레이터가 트랜잭션을 주도하고, 모드는 이벤트 보고/지시 수행만 한다.
public sealed class MigrationModule : MonoBehaviour
{
    private readonly HashSet<string> _reported = new();
    private static readonly HashSet<string> _loadingTriggered = new();   // 10016(조기 로딩) 발신 기록 — 중단 복구용
    // 동결 플레이어 → 현재 epoch. 구 epoch 메시지는 무시한다.
    private static readonly ConcurrentDictionary<string, int> FrozenPlayers = new();

    // 클라이언트의 월드젠 완료 통지는 실제 로컬 몸 준비보다 ~2.9초 빠르므로, RELEASE 후
    // 4초 유예를 둬 "NO LOCAL NETBODY"를 방지한다.
    private const float ReleaseGraceSeconds = 4.0f;

    public static bool IsFrozen(string persistentId) => FrozenPlayers.ContainsKey(persistentId);

    // 목적지 접속은 끝났지만 RESUME이 아직 도착하지 않은 구간도 동기화를 막는다.
    public static bool IsArrivalBlocked(NetPlayer plr) =>
        plr != null && MigrationArrivalTracker.ClientIds.Contains(plr.clientId);

    public static int FrozenEpoch(string persistentId) =>
        FrozenPlayers.TryGetValue(persistentId, out int epoch) ? epoch : -1;

    private void Update()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
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
                // 로딩 트리거(10016)는 FREEZE 처리에서 수행한다 — 여기서 먼저 보내면
                // FREEZE의 10016과 중복되어 클라이언트 재생성이 손상된다.
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

    // 명령 처리 (오케스트레이터 → 모드).

    // 리스폰 트랜잭션 여부 — 죽은 상태의 캡처를 금지하고 목적지에서 프레시 신규 상태를 보장한다.
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
            SaveModule.SubmitPlayer(plr);   // 인벤토리 캡처
        }
        else
        {
            // 리스폰: 캡처 금지 — 목적지에서 프레시 신규 상태를 보장한다.
        }

        // 레지스트리 정리 버스트 — ReliableOrdered 직접 전송으로 클라이언트의 옛 객체를
        // 결정적으로 삭제한다. 파괴 후 보내면 등록 해제된 아이템이 server_objects에 없어
        // 유령 아이템이 잔존하므로 먼저 보낸다.
        SendRegistryCleanupBurst(plr);

        // 인벤토리/착용 아이템을 월드에 드랍하지 않고 파괴한다 (드랍은 복사/유령 아이템
        // 위험 — SafeDestroyObject의 forcesync 적재는 중복·무해).
        try
        {
            // 자식부터 파괴해 컨테이너가 내용물을 월드에 풀어놓는 중간 상태를 줄인다.
            var items = new List<Item>(plr.body.GetAllItemsThorough());
            items.Reverse();

            var destroyed = new HashSet<Item>();
            foreach (Item item in items)
            {
                if (item == null || !destroyed.Add(item)) continue;
                NetObjectRegistry.SafeDestroyObject(item.gameObject);
            }

            // body.slots는 InventorySlot[] 컴포넌트 배열 — null로 비우면 슬롯 기능이 망가진다.
            // 슬롯 안 아이템은 위 경로에서 정리되므로 슬롯 자체는 그대로 둔다.
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] {playerKey} 인벤토리 파괴 실패: {ex.Message}");
        }

        // 다른 클라이언트에 10170(퇴장) 즉시 브로드캐스트 — SWAP 디스커넥트 전에 바디가
        // 화면에서 제거되게 한다. 본인 수신 시 LOCAL_PLAYER가 파괴되므로 반드시 제외.
        SendPlayerLeftToOthers(plr);
        // 발신 중단은 QuiescencePatches의 게이트가 담당 (IsFrozen).

        // 1초 후: 10016(로딩 화면) + FREEZE_DONE — 버스트/파괴 정리 시간 확보.
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(1f, (Action)(() =>
        {
            if (plr == null) return;
            SendRegenerateWorld(plr);
            _loadingTriggered.Add(playerKey);
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
        }));
    }

    // 클라이언트의 client_objects 레지스트리는 forcedelete(10174, result4==0)로만 제거되고
    // 월드 재생성(10016) 시 정리되지 않는다 — 목적지가 낮은 netId를 재사용할 때 스테일
    // 항목에 부딪혀 프리팹 오염 + 파괴된 바디 Apply NRE가 발생한다. 객체/CharSync/PlrSync
    // 전 등록 객체의 forcedelete를 일괄 전송해 레지스트리를 비운다 (본인 바디 제외).
    // 서버 PackAndSend의 와이어 형식을 미러링한다 ([systemid][deltaid][count][netId,0]…).
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
                bool excludeSelf = systemId == 2 || systemId == 3; // CharSync/PlrSync — 본인 제외

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

    // 단일 시스템의 모든 등록 객체를 대상 플레이어에게 forcedelete로 일괄 전송.
    // 반환값: (패킷 수 << 16) | 객체 수.
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
            // 10174 형식 미러링: [systemid][deltaid][count][netId,0]… + 압축.
            const int chunkSize = 120;
            for (int i = 0; i < netIds.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, netIds.Count - i);
                var writer = Net.CreateWriter(10174);
                writer.Put(systemId);
                int startToIgnore = writer.Length + 2;
                writer.Put((ushort)0); // deltaid — 삭제는 값 무시
                writer.Put((byte)count);
                for (int j = 0; j < count; j++)
                {
                    writer.Put(netIds[i + j]);
                    writer.Put((byte)0); // result4 == 0 → 삭제
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

    // 10170(플레이어 퇴장)을 이 인스턴스의 다른 클라이언트 전체에 브로드캐스트 (본인 제외).
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

        // 중단 복구: 10016(조기 로딩)을 보냈던 플레이어면 구 월드로 복귀시킨다 —
        // 클라이언트는 여전히 로딩 화면이므로 10016 재전송 + 구 월드 파라미터 announce로
        // 재생성시킨다. (스왑 후 UNFREEZE는 플레이어가 이미 새 백엔드에 있어 무해)
        if (_loadingTriggered.Remove(playerKey))
        {
            NetPlayer plr = FindByPersistentId(playerKey);
            if (plr != null)
            {
                // 파괴된 인벤토리/객체의 스테일 항목 정리 후 구 월드 재생성 (FREEZE와 동일 순서).
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

                // pre-swap 중단: 인벤토리는 파괴됨 — 캡처 데이터로 즉시 재적용한다.
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
        // 리스폰은 payload가 의도적으로 없다 (프레시 신규) — 형식 오류로 보지 않는다.
        if (!respawn && (payload == null || payload.Type != JTokenType.Object))
        {
            Plugin.Log.LogWarning($"[Migration] RESUME 형식 오류: {playerKey}");
            OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
            return;
        }
        if (respawn)
        {
            // 데이터 보관 없음 — 목적지 Body.Start가 프레시 생성 + 보급품 지급.
            SaveModule.RemovePending(playerKey);
        }
        else
        {
            SaveModule.SetPending(playerKey, (JObject)payload);
        }
        // 이 인스턴스에서도 마이그레이션 대상으로 동결 — WORLDGEN_DONE 보고와 퇴장 저장
        // 스킵에 필요. 중복 RESUME은 데이터 덮어쓰기 + epoch 재설정으로 멱등하다.
        FrozenPlayers[playerKey] = epoch;

        // 이전 레이어에 남아있는 다른 플레이어들의 잔존 NetPlayer를 즉시 제거하도록
        // 10170(퇴장 신호)을 도착자에게 보낸다 (새 인스턴스는 그들을 모르므로 자연 정리 불가).
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
        // 로딩 트리거는 구 인스턴스의 FREEZE가 단일 발신원 — 재전송 시 재생성이 손상된다.

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

        // RELEASE를 받았다고 바로 동기화를 재개하면 아직 몸이 없는 클라이언트가 10174를
        // 받아 터진다 — 실제 준비까지 최대 ~2.9초이므로 유예 후 동결을 해제한다.
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(ReleaseGraceSeconds, (Action)(() =>
        {
            if (!FrozenPlayers.TryGetValue(playerKey, out int delayedCurrent)) return;
            if (epoch >= 0 && delayedCurrent != epoch) return;

            FrozenPlayers.TryRemove(playerKey, out _);
            _loadingTriggered.Remove(playerKey);

            // 도착 차단(조기 동기화 방지) 상태도 해제.
            NetPlayer released = FindByPersistentId(playerKey);
            if (released != null)
            {
                MigrationArrivalTracker.ClientIds.Remove(released.clientId);
            }
        }));
    }

    // 클라이언트에 10016(RegenerateWorld) 전송 — 즉시 로딩 화면 진입.
    private static void SendRegenerateWorld(NetPlayer plr)
    {
        if (plr == null) return;
        IEnumerable<knetid> targets = new List<knetid> { plr.clientId };
        var writer = Net.CreateWriter(10016);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.doPod ? (ushort)1 : (ushort)0);
        Net.Server_SendToClientsVeryReliable(in writer, in targets);
    }

    // 클라이언트에 10170(플레이어 퇴장) 전송. 서버 발신 프로토콜: [ushort clientId][ulong steam_id]
    // — steam_id는 클라이언트 수신기가 읽지 않으므로 0으로 전송한다.
    private static void SendPlayerLeft(ushort leavingClientId, List<knetid> targets)
    {
        var writer = Net.CreateWriter(10170);
        writer.Put(leavingClientId);
        writer.Put(0UL);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    // 후킹.

    // 월드젠 완료 보고 + 신원 재브로드캐스트 — 수신자별 finished_worldgen 이후 발신이므로
    // 로딩 중 유실이 없어 "나중 마이그레이션 유저가 안 보이는" 레이스를 결정적으로 해결한다.
    [HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_FinishedWorldgen")]
    internal static class ServerReceiver_FinishedWorldgen_ReportPatch
    {
        private static void Postfix(knetid clientId)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
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

    // 월드젠 완료 플레이어 기준 신원 교환: 나의 신원 → 다른 클라이언트들, 타인 신원 → 나.
    // 바디 CharSync DELETE 없이 신원 교환(10023)만으로 관찰자 바디가 실제 플레이어 바디로
    // 매핑되도록 한다 — DELETE는 파괴 지연 레이스로 바디 미표시/스턱을 유발한다.
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

    // 동결된 플레이어의 퇴장 → PLAYER_LEFT (오케스트레이터가 데이터 확정). 동시에 퇴장자의
    // CharSync 바디를 서버측에서 제거한다 — 바닐라 OnDestroy는 CharSync 항목을 고아로 남겨
    // 같은 clientId 재접속 시 파괴된 바디에 Apply하는 NRE의 근본 원인이 된다.
    // 정리는 Postfix에서 수행 — vanilla OnDestroy가 플레이어를 목록에서 제거한 뒤 실행되므로
    // Server_DeleteObject 브로드캐스트가 본인을 자연히 제외한다.
    [HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.OnDestroy))]
    internal static class NetPlayer_OnDestroy_ReportLeftPatch
    {
        private static void Prefix(NetPlayer __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if (__instance == null || __instance.is_local) return;
            string pid = __instance.GetPersistentId();
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("PLAYER_LEFT",
                    new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
            else
            {
                // 완전 퇴장(마이그레이션 아님) — 전 레이어 공지.
                AnnounceRelay.SendLeave(__instance);
            }
        }

        private static void Postfix(NetPlayer __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if (__instance == null || __instance.is_local) return;

            // CharSync 바디 고아화 방지 (일반 퇴장 포함 전 케이스).
            // 서버측 객체 제거 — 고아 동기화 중단.
            // 신뢰성 10174 DELETE를 남은 클라이언트 전체에 즉시 전송 — 비신뢰 채널의
            //    Server_DeleteObject 삭제 패킷은 재접속 재동기화와 레이스하므로, 신뢰성
            //    채널로 먼저 항목을 확실히 제거한다 (재접속은 수 초 후 — 안전한 간격).
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

    // 퇴장 플레이어의 CharSync 바디 항목을 남은 클라이언트 전체에서 제거
    // (10174 system 2, result4=0 — ReliableOrdered).
    private static void SendReliableCharSyncDelete(ushort leavingClientId)
    {
        List<knetid> targets = NetPlayer.ClientIdToPlayerDict.Keys
            .Where(id => id != leavingClientId)
            .ToList();
        if (targets.Count == 0) return;

        var writer = Net.CreateWriter(10174);
        writer.Put((byte)2);          // CharSync
        int startToIgnore = writer.Length + 2;
        writer.Put((ushort)0);        // deltaid — 삭제는 값 무시
        writer.Put((byte)1);
        writer.Put(leavingClientId);
        writer.Put((byte)0);          // result4 == 0 → 삭제
        writer.CompressWriter(startToIgnore);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    private static NetPlayer FindByPersistentId(string persistentId)
    {
        return NetPlayer.ClientIdToPlayerDict.Values
            .FirstOrDefault(p => p.GetPersistentId() == persistentId);
    }
}
