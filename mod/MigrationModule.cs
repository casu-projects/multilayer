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

/// 오케스트레이터가 트랜잭션을 주도하고, 모드는 이벤트 보고/지시 수행만 한다.
public sealed class MigrationModule : MonoBehaviour
{
    private readonly HashSet<string> _reported = new();
    private static readonly HashSet<string> _loadingTriggered = new();   // 10016(조기 로딩) 발신 기록 — 중단 복구용
    /// <summary>동결 플레이어 → 현재 epoch. 구 epoch 메시지는 무시한다 (P2 멱등).</summary>
    private static readonly ConcurrentDictionary<string, int> FrozenPlayers = new();

    public static bool IsFrozen(string persistentId) => FrozenPlayers.ContainsKey(persistentId);

    /// <summary>동결된 플레이어의 현재 epoch (미동결이면 -1).</summary>
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
                // 로딩 트리거(10016)는 FREEZE 처리에서 수행한다 (캡처→인벤토리 파괴→1초 플러시→10016).
                // 여기서 먼저 보내면 FREEZE의 10016과 중복되어 클라이언트 재생성이
                // 취소·재시작되고 상태가 손상된다.
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

    // ── 명령 처리 (오케스트레이터 → 모드) ──

    /// <summary>리스폰 트랜잭션 여부 — 죽은 상태의 캡처(SubmitPlayer)를 금지하고
    /// 목적지에서 프레시 신규 상태를 보장한다 (리스폰 = 완전 신규).</summary>
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
            Plugin.Log.LogWarning($"[Migration] FREEZE: {playerKey} 바디 없음.");
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
            return;
        }

        FrozenPlayers[playerKey] = epoch;
        if (!respawn)
        {
            SaveModule.SubmitPlayer(plr);   // S9-6: 직렬화 + 제출 (인메모리 핸드오프용) — 인벤토리 캡처
        }
        else
        {
            // 리스폰: 캡처 금지 — 죽은 상태 데이터가 목적지에 전달되면 리스폰이 실패한다
            // (프레시 신규 상태 보장 — 세이브는 오케스트레이터가 이미 폐기).
        }

        //    등록 상태여서 버스트에 포함된다. ReliableOrdered 직접 전송이라 forcesync 큐
        //    (사망 플레이어 한도 20 — Server_RunFastSync가 초과 시 건너뜀)에 의존하지 않고
        //    결정적으로 클라이언트에 삭제가 전달된다. 파괴 후 버스트를 보내면 등록 해제된
        //    아이템이 server_objects에 없어 유령 아이템이 잔존한다.
        SendRegistryCleanupBurst(plr);

        // ② 인벤토리/착용 아이템을 월드에 드랍하지 않고 파괴한다 (클라이언트는 ①에서 이미
        //    삭제됨 — 여기서는 서버 상태 정리. SafeDestroyObject의 forcesync 적재는 중복·무해).
        //    드랍 방식은 다른 유저가 주워 아이템을 복사할 수 있어 채택하지 않는다.
        try
        {
            foreach (Item item in plr.body.GetAllItemsThorough())
            {
                if (item == null) continue;
                NetObjectRegistry.SafeDestroyObject(item.gameObject);
            }
            foreach (Item wearable in plr.body.GetAllWearables())
            {
                if (wearable == null) continue;
                NetObjectRegistry.SafeDestroyObject(wearable.gameObject);
            }
            for (int i = 0; i < plr.body.slots.Length; i++)
            {
                plr.body.slots[i] = null;
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] {playerKey} 인벤토리 파괴 실패: {ex.Message}");
        }

        // "레이어 끝에 닿자마자 바디가 사라지는" UX (P3): 다른 클라이언트에 10170(퇴장)을
        // 즉시 브로드캐스트 — 바디는 SWAP 디스커넥트 전에 화면에서 제거된다. 반드시 본인
        // 제외 (본인이 수신하면 LOCAL_PLAYER가 파괴된다).
        SendPlayerLeftToOthers(plr);
        // 발신 중단은 QuiescencePatches의 ShouldPackPacketFor/PackObjectForPlr 게이트가 담당
        // (IsFrozen — CharSync 바디/PlrSync/객체 스트림 전부).

        // ③ 1초 후: 10016(로딩 화면) + FREEZE_DONE — SWAP이 구 인스턴스 연결을 끊기 전에
        //    버스트/파괴가 정리될 시간 확보 (순서: 버스트→파괴→1초→10016→완료).
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(1f, (Action)(() =>
        {
            if (plr == null) return;
            SendRegenerateWorld(plr);
            _loadingTriggered.Add(playerKey);
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
        }));
    }

    /// 클라이언트의 client_objects 레지스트리는 forcedelete 패킷(10174, result4==0)으로만
    /// 제거되고 월드 재생성(10016) 시 정리되지 않는다 — 출발지 월드의 객체/타인 바디가
    /// SWAP으로 백엔드가 끊겨 삭제 패킷 없이 고아로 잔존하고, 목적지 인스턴스가 낮은
    /// netId를 재사용하면서 스테일 항목에 부딪혀 옛 itemid로 프리팹 오염(emergencylight
    /// 버그) + 파괴된 바디 상태 적용 NRE가 발생한다. 이주 플레이어에게 3개 시스템
    /// (객체/CharSync/PlrSync)의 전 등록 객체 forcedelete를 일괄 전송해 레지스트리를
    /// 비운다 — 목적지의 netId 재사용이 안전해진다. 본인 바디는 재생성이 유지되므로 제외.
    /// 서버 PackAndSend의 와이어 형식을 그대로 미러링한다 ([systemid][deltaid][count]
    /// [netId,0]… + CompressWriter).</summary>
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

    /// <summary>단일 시스템의 모든 등록 객체를 대상 플레이어에게 forcedelete로 일괄 전송.
    /// 반환값: (패킷 수 &lt;&lt; 16) | 객체 수.</summary>
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

    /// <summary>10170(플레이어 퇴장)을 이 인스턴스의 다른 클라이언트 전체에 브로드캐스트.
    /// 대상에서 이주 플레이어 본인은 반드시 제외 (본인 수신 시 LOCAL_PLAYER 파괴 위험).</summary>
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
        // 클라이언트는 여전히 로딩 화면(파라미터 대기)이므로 10016 재전송 + 구 월드
        // 파라미터 announce로 재생성시킨다. (스왑 후 UNFREEZE는 플레이어가 이미 새
        // 백엔드에 연결되어 있어 이 인스턴스의 패킷이 도달하지 않으므로 무해)
        if (_loadingTriggered.Remove(playerKey))
        {
            NetPlayer plr = FindByPersistentId(playerKey);
            if (plr != null)
            {
                // FREEZE에서 파괴된 인벤토리/객체의 스테일 항목을 먼저 정리 후 구 월드
                // 재생성 (10016 이전에 삭제 버스트 — FREEZE 경로와 동일한 순서 원칙).
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

                // pre-swap 중단: 인벤토리는 FREEZE에서 이미 파괴됨 — 캡처 데이터로 즉시
                // 재적용한다 (땅 드랍 없이 복원 — 드랍을 다시 주울 필요가 없음).
                // 위치는 RestorePlayer의 QueuePosition이 적재하며, 구 월드 재생성 후 바닐라
                // LateSpawnLocation 시점(저장 위치 Prefix)에 적용된다.
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
            // S9-6: 데이터 보관 없음 — 목적지 Body.Start가 "데이터 없음" 경로로 프레시 생성 +
            // 시작 보급품 자동 지급. 잔존 엔트리가 있으면 정리 (중복/스테일 방어).
            SaveModule.PendingData.TryRemove(playerKey, out _);
        }
        else
        {
            // S9-6: 데이터 보관 → 바디 생성 시(Body.Start) 복원 경로 사용
            SaveModule.PendingData[playerKey] = (JObject)payload;
        }
        // 이 인스턴스에서도 마이그레이션 대상으로 동결 — 10168(WORLDGEN_DONE) 보고와
        // 퇴장 보고/저장 스킵에 필요 (FREEZE는 구 인스턴스에만 도착하므로 RESUME 시점에 설정).
        // 재전송/복구(중복 RESUME)는 데이터 덮어쓰기 + epoch 재설정으로 멱등하다.
        FrozenPlayers[playerKey] = epoch;

        // 이전 레이어에 남아있는 다른 플레이어들 정리 — 도착 플레이어의 클라이언트가
        // 잔존 NetPlayer(위치/방향 표시)를 즉시 제거하도록 10170(퇴장 신호)을 보낸다.
        // 새 인스턴스는 그들을 모르므로 바닐라가 자연 정리하지 못해 지연 잔존이 생긴다.
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
        // 로딩 트리거는 구 인스턴스의 FREEZE(1초 플러시 후 10016)가 단일 발신원이다.
        // 여기서 재전송하면 대기 중인 클라이언트의 재생성이 취소·재시작되어 상태가 손상된다.

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
        FrozenPlayers.TryRemove(playerKey, out _);
        _loadingTriggered.Remove(playerKey);
    }

    /// <summary>클라이언트에 10170(RegenerateWorld) 전송 — 즉시 로딩 화면 진입.
    /// 이후 목적지 인스턴스의 10010(파라미터 announce)으로 대기가 해소된다.</summary>
    private static void SendRegenerateWorld(NetPlayer plr)
    {
        if (plr == null) return;
        IEnumerable<knetid> targets = new List<knetid> { plr.clientId };
        var writer = Net.CreateWriter(10016);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.doPod ? (ushort)1 : (ushort)0);
        Net.Server_SendToClientsVeryReliable(in writer, in targets);
    }

    /// <summary>클라이언트에 10170(플레이어 퇴장) 전송 — 마이그레이션 후 잔존하는
    /// clientId를 읽고 해당 NetPlayer 파괴). 서버 발신 프로토콜: [ushort clientId][ulong steam_id]
    /// — steam_id는 클라이언트 수신기가 읽지 않으므로 0으로 전송한다.</summary>
    private static void SendPlayerLeft(ushort leavingClientId, List<knetid> targets)
    {
        var writer = Net.CreateWriter(10170);
        writer.Put(leavingClientId);
        writer.Put(0UL);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, targets);
    }

    // ── 후킹 ──

    /// 재전송하고, 그 신원을 타인에게 일괄 재브로드캐스트한다 (수신자별 finished_worldgen
    /// 이후 발신이므로 로딩 중 유실이 원천적으로 불가능 — "나중 마이그레이션 유저가
    /// 안 보이는" 레이스의 결정적 해결).</summary>
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

    /// <summary>월드젠 완료 플레이어를 기준으로 신원 교환을 결정적으로 완료한다:
    /// ① 나의 신원 → 다른 모든 클라이언트 ② 다른 모든 플레이어의 신원 → 나.
    ///
    /// 2026-08-03: 바디 DELETE(전행렬/로스터/월드젠 정리) 전부 제거 — 살아있는 플레이어
    /// 바디의 CharSync 항목을 삭제하면, 같은 프레임의 재동기화가 파괴 지연(DestroyNPC —
    /// 프레임 말미) 전의 기존 바디를 반환하고(plr.body != null 경로), 이후 파괴된 바디에
    /// Apply → NRE + 바디 미표시 영구 스턱이 발생한다 (2026-08-03 실측). 신원(10023)은
    /// 바디 CharSync와 별개 채널이므로 삭제 없이도 안전하게 교환된다.
    ///
    /// 복귀 DELETE 제거 (2026-08-03): 재접속/마이그레이션 도착 시 관찰자들의 도착자 바디
    /// 항목에 신뢰성 DELETE를 발신했으나, 실제 바디(기존바디 경로)까지 파괴하고 재생성
    /// (다음 동기화)이 실패해 — 바디 미표시 + shift 누락이 양쪽 클라이언트에 대칭 발생
    /// (실측: 마이그레이션 후 서로 보이지 않음). 신원 교환(10023)만으로 관찰자 바디는
    /// 자연 동기화가 실제 플레이어 바디로 생성·매핑되므로, DELETE 없이도 shift가 정상
    /// 동작한다 (dev 최초 재접속 정상 케이스로 검증).</summary>
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

    /// <summary>동결(FREEZE)된 플레이어의 퇴장 → PLAYER_LEFT (오케스트레이터가 데이터 확정).
    /// 동시에 (2026-08-03, Fix S5) 퇴장 플레이어의 CharSync 바디 객체를 서버측에서
    /// 제거한다 — 바닐라 OnDestroy는 PlrSync만 삭제하고 CharSync 바디는 고아로 남겨
    /// 동기화가 계속된다. 10170으로 클라이언트 바디가 파괴되어도 CharSync 항목이 스테일로
    /// 잔존하고, 같은 clientId(같은 netId)로 재접속/마이그레이션 시 재동기화가 파괴된
    /// 바디에 Apply → Client_CoolSyncReceiver(10174) NRE 스팸의 근본 원인.
    /// Server_DeleteObject는 서버 객체 제거 + result4=0 삭제 패킷으로 모든 클라이언트의
    /// 항목을 깨끗이 제거하므로, 재접속 시 생성 분기로 클린 등록된다.
    /// S5 정리는 Postfix에서 수행한다 (2026-08-03 수정): vanilla OnDestroy가 플레이어를
    /// 클라이언트 목록에서 제거한 뒤 실행되므로 Server_DeleteObject 브로드캐스트가
    /// 퇴장/마이그레이션 플레이어 본인을 자연히 제외한다. Prefix에서 실행하면 본인에게도
    /// DELETE가 도달해 — 마이그레이션 목적지 월드젠 완료 직후 본인 바디가 파괴되고
    /// PlayerCamera/Observer NRE 폭풍 + 로딩 화면 프리즈가 발생한다 (2026-08-03 실측).</summary>
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
                // 완전 퇴장(마이그레이션 아님) — 전 레이어 공지 (ANNOUNCE 릴레이).
                // 마이그레이션은 frozen 상태로 PLAYER_LEFT만 보내므로 공지되지 않는다.
                AnnounceRelay.SendLeave(__instance);
            }
        }

        private static void Postfix(NetPlayer __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if (__instance == null || __instance.is_local) return;

            // S5 — CharSync 바디 고아화 방지 (일반 퇴장 포함 전 케이스).
            // ① 서버측 객체 제거 (고아 동기화 중단 — 삭제-재생성 창의 NPC 재생성 방지).
            // ② 신뢰성 있는 raw 10174 DELETE를 남은 클라이언트 전체에 즉시 전송 —
            //    바닐라 10170(신뢰성)이 클라이언트 바디를 파괴해도 CharSync 항목이
            //    스테일로 잔존하고, 같은 clientId 재접속의 재동기화가 파괴된 바디에
            //    Apply → NRE 스팸 + 미표시. Server_DeleteObject의 삭제 패킷은 비신뢰
            //    CharSync 채널이라 재접속 재동기화와 레이스하므로, 신뢰성 채널로
            //    먼저 항목을 확실히 제거한다 (재접속은 수 초 후 — 안전한 간격).
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

    /// <summary>퇴장 플레이어의 CharSync 바디 항목을 남은 클라이언트 전체에서 제거
    /// (10174 system 2, result4=0 — ReliableOrdered). 바닐라 10170이 클라이언트 바디를
    /// 파괴해도 CharSync 항목은 잔존하므로, 재접속(같은 clientId=같은 netId) 재동기화가
    /// 파괴된 바디에 Apply하는 NRE 스턱을 방지한다. 본인(퇴장)은 연결 종료로 수신 불가 —
    /// 포함해도 무해하나 대상 목록에서 제외한다.</summary>
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
