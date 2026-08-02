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

/// <summary>마이그레이션 (O3/S9-6) — LAYER_END 감지 + FREEZE/RESUME/TRIGGER_WORLDGEN/RELEASE.
/// 오케스트레이터가 트랜잭션을 주도하고, 모드는 이벤트 보고/지시 수행만 한다.
/// P2/P3: epoch 멱등 + FREEZE 시 발신 중단(10170 브로드캐스트) + 월드젠 완료 시 로스터 배리어.</summary>
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
                Plugin.Log.LogInfo($"[Migration] {p.playername} 레이어 끝 도달 — LAYER_END 보고 (depth {fromDepth}).");
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
            Plugin.Log.LogInfo($"[Migration] {playerKey} FREEZE — 리스폰 캡처 스킵.");
        }

        // 인벤토리/착용 아이템을 월드에 드랍하지 않고 클라이언트 동기화와 함께 파괴한다.
        // 드랍 방식은 다른 유저가 주워 아이템을 복사할 수 있고, 파괴 없이 두면 클라이언트가
        // 구 netId 아이템을 새 레이어로 가져가 유령 아이템이 된다. SafeDestroyObject는
        // forcesync로 클라이언트의 복사본 제거를 통지한다 (유령 아이템 원천 차단).
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
            Plugin.Log.LogInfo($"[Migration] {playerKey} FREEZE — 인벤토리 동기화 파괴 (데이터는 캡처됨).");
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

        // 파괴 forcesync(틱 ~0.2초)가 클라이언트에 도달·처리된 뒤 로딩 화면과 FREEZE_DONE을
        // 보낸다 — SWAP이 구 인스턴스 연결을 끊기 전에 플러시를 보장 (순서: 캡처→파괴→1초→10016→완료).
        KrokoshaCasualtiesUtils.Util.DelayCallLambda(1f, (Action)(() =>
        {
            if (plr == null) return;
            SendRegenerateWorld(plr);
            SendRegistryCleanupBurst(plr);   // Fix I — 클라이언트 CoolSync 레지스트리 정리
            _loadingTriggered.Add(playerKey);
            Plugin.Log.LogInfo($"[Migration] {playerKey} FREEZE — 로딩 트리거 + 완료 보고.");
            OrchestratorClient.Instance?.SendEvent("FREEZE_DONE", new { playerKey, epoch });
        }));
    }

    /// <summary>클라이언트 CoolSync 레지스트리 정리 버스트 (2026-08-02, Fix I).
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
                Plugin.Log.LogInfo($"[Migration] {plr.playername} 레지스트리 정리 버스트 — {sentObjects}개 객체 / {sentPackets}패킷.");
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Migration] 레지스트리 정리 버스트 실패: {ex.Message}");
        }
    }

    /// <summary>월드젠 완료(10168) 시 바디 레지스트리 정리 (2026-08-02, Fix I2).
    /// 출발지 정리 버스트는 FREEZE 시점에 출발지에 존재하는 바디만 삭제한다 — 먼저
    /// 마이그레이션한 유저의 바디는 이미 출발지에서 제거되어 버스트에 포함되지 않고,
    /// SWAP으로 삭제 패킷도 도달하지 않아 나중 마이그레이터 클라이언트에 스테일 CharSync
    /// 항목(netId=clientId — 인스턴스 간 동일)이 잔존한다. 목적지 바디 동기화가
    /// finished_worldgen+RELEASE 이후 시작되는데 이 삭제가 그보다 먼저 도착하므로,
    /// 스테일 항목을 비우고 목적지 CharSync가 새 바디를 생성하게 한다 (재접속/신규
    /// 조인에도 동일 적용 — 프레시 클라이언트는 no-op).</summary>
    private static void SendBodyCleanupOnWorldgen(NetPlayer plr)
    {
        if (plr == null) return;
        foreach (BaseCoolSyncSubSystem sys in CoolSyncManager.AllSystems)
        {
            if (sys.syncsystemid != 2) continue; // CharSync(바디)만
            int sent = SendDeleteBurstForSystem(plr, sys, 2, excludeSelf: true);
            if ((sent & 0xFFFF) > 0)
            {
                Plugin.Log.LogInfo($"[Migration] {plr.playername} 월드젠 완료 — 바디 레지스트리 정리 ({sent & 0xFFFF}개).");
            }
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
            Plugin.Log.LogInfo($"[Migration] {leaving.playername}({leaving.clientId}) 퇴장 신호 → {targets.Count}명 브로드캐스트.");
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
            Plugin.Log.LogInfo($"[Migration] {playerKey} UNFREEZE 무시 (epoch {epoch} — 현재 {current}).");
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
                SendRegenerateWorld(plr);
                try
                {
                    ServerMain.Server_AnnounceSeed(new List<knetid> { plr.clientId });
                    Plugin.Log.LogInfo($"[Migration] {playerKey} UNFREEZE — 구 월드 재생성 복구.");
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Migration] UNFREEZE 복구 announce 실패: {ex.Message}");
                }

                // pre-swap 중단: 인벤토리는 FREEZE에서 이미 파괴됨 — 캡처 데이터로 즉시
                // 재적용한다 (땅 드랍 없이 복원 — 드랍을 다시 주울 필요가 없음)
                JToken payload = msg.Inner("payload");
                if (payload != null && payload.Type == JTokenType.Object && plr.body != null)
                {
                    try
                    {
                        SaveModule.RestorePlayer(plr.body, plr, (JObject)payload);
                        SaveModule.ApplyPendingPositions();
                        Plugin.Log.LogInfo($"[Migration] {playerKey} UNFREEZE — 데이터 재적용 완료.");
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Migration] {playerKey} UNFREEZE 데이터 재적용 실패: {ex.Message}");
                    }
                }
            }
        }
        Plugin.Log.LogInfo($"[Migration] {playerKey} UNFREEZE.");
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
            Plugin.Log.LogInfo($"[Migration] {playerKey} RESUME — 리스폰 (프레시 신규, 데이터 없음).");
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
                Plugin.Log.LogInfo($"[Migration] {playerKey} RESUME — 이전 레이어 플레이어 {sent}명 정리 신호.");
            }
        }
        // 로딩 트리거는 구 인스턴스의 FREEZE(1초 플러시 후 10016)가 단일 발신원이다.
        // 여기서 재전송하면 대기 중인 클라이언트의 재생성이 취소·재시작되어 상태가 손상된다.

        Plugin.Log.LogInfo($"[Migration] {playerKey} RESUME — 데이터 보관 + 동결 (epoch {epoch}).");
        OrchestratorClient.Instance?.SendEvent("RESUME_DONE", new { playerKey, epoch });
    }

    internal static void HandleTriggerWorldgen(string playerKey)
    {
        NetPlayer plr = FindByPersistentId(playerKey);
        if (plr == null)
        {
            Plugin.Log.LogWarning($"[Migration] TRIGGER_WORLDGEN: {playerKey} 플레이어 없음.");
            return;
        }

        SendRegenerateWorld(plr);
        Plugin.Log.LogInfo($"[Migration] {playerKey} TRIGGER_WORLDGEN (10016 전송).");
    }

    internal static void HandleRelease(string playerKey, int epoch)
    {
        if (!FrozenPlayers.TryGetValue(playerKey, out int current) || (epoch >= 0 && epoch != current))
        {
            Plugin.Log.LogInfo($"[Migration] {playerKey} RELEASE 무시 (epoch {epoch} — 현재 {current}).");
            return;
        }
        FrozenPlayers.TryRemove(playerKey, out _);
        _loadingTriggered.Remove(playerKey);
        Plugin.Log.LogInfo($"[Migration] {playerKey} RELEASE — 소유권 이전 완료.");
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
    /// 이전 레이어 플레이어들의 NetPlayer를 즉시 정리한다 (ClientReceiver__PlayerLeft:
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

    /// <summary>10168(월드젠 완료) 수신 → WORLDGEN_DONE 보고 (마이그레이션 커밋 신호).
    /// P3 로스터 배리어: 월드젠 완료는 전역 동기화 기준점 — 완료한 플레이어에게 전 로스터를
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

            // 저장 위치 지연 적용 (2026-08-02): 바닐라 스폰 리로케이션(10019, 10167 후 전송)이
            // Body.Start 시점에 적용한 저장 위치를 덮어쓰는 경합을 해결한다 — 여기서(10168,
            // 바닐라 스폰 이후) 다시 적용하면 저장 위치가 최종 반영된다. 레이어 불일치
            // (마이그레이션 도착)는 내부 로직이 기본 스폰으로 드랍한다.
            try { SaveModule.ApplyPendingPositions(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Save] 위치 지연 적용 실패: {ex.Message}"); }

            // 로스터 배리어 — 멱등 (중복 수신은 이름/색 갱신만)
            try { RosterBarrier(plr); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Roster] 배리어 실패: {ex.Message}"); }

            // Fix I2 — 목적지 측 바디 레지스트리 정리 (스테일 CharSync 항목 제거).
            // 목적지 바디 동기화가 이 뒤에 시작되므로, 먼저 마이그레이터의 스테일 바디
            // 항목이 새 바디 생성을 막는 문제를 해소한다.
            try { SendBodyCleanupOnWorldgen(plr); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Migration] 월드젠 바디 정리 실패: {ex.Message}"); }

            string pid = plr.GetPersistentId();
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("WORLDGEN_DONE", new { playerKey = pid, epoch = FrozenEpoch(pid) });
                Plugin.Log.LogInfo($"[Migration] {plr.playername} WORLDGEN_DONE 보고.");
            }
        }
    }

    /// <summary>월드젠 완료 플레이어를 기준으로 신원 교환을 결정적으로 완료한다:
    /// ① 나의 신원 → 다른 모든 클라이언트 ② 다른 모든 플레이어의 신원 → 나.</summary>
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
        Plugin.Log.LogInfo($"[Roster] {plr.playername}({plr.clientId}) 로스터 배리어 — 타인 {others.Count}명.");
    }

    /// <summary>동결(FREEZE)된 플레이어의 퇴장 → PLAYER_LEFT (오케스트레이터가 데이터 확정).</summary>
    [HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.OnDestroy))]
    internal static class NetPlayer_OnDestroy_ReportLeftPatch
    {
        private static void Prefix(NetPlayer __instance)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
            if (__instance == null) return;
            string pid = __instance.GetPersistentId();
            if (IsFrozen(pid))
            {
                OrchestratorClient.Instance?.SendEvent("PLAYER_LEFT",
                    new { playerKey = pid, epoch = FrozenEpoch(pid) });
            }
        }
    }

    private static NetPlayer FindByPersistentId(string persistentId)
    {
        return NetPlayer.ClientIdToPlayerDict.Values
            .FirstOrDefault(p => p.GetPersistentId() == persistentId);
    }
}
