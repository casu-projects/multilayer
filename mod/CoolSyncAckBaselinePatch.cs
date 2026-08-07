using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod;

/// <summary>CoolSync 스냅샷 백로그 근본 수정 (2026-08-06, Fix A — v2: Postfix 구조).
/// 문제: 베이스 Server_OneStepClearOldSnapshots가 "어떤 객체의 last_known_snapshot_id == 큐 맨 앞
/// 스냅샷"이면 정리를 블록한다(CoolSyncSubSystemForObjects.cs:576). 객체 기준선은 그 객체가 새
/// 스냅샷에 포함돼 ACK될 때만 전진하므로(L326-331), 한 번 변경 후 정지한 "쿨" 객체가 옛 기준선을
/// 영원히 보유 → 큐 정리 영구 블록 → 스냅샷 1500+ 축적(TOO MUCH SNAPSHOTS 경고) + 2000 상한 강제
/// 트림(기준선 파괴 → 재동기화).
/// 수정: ACK 도착 시 해당 플레이어의 objstate 기준선 ID를 ACK된 스냅샷으로 일괄 전진.
/// 클라는 그 스냅샷까지 전부 수신했으므로 의미상 정확하고, 델타 기준(패킷)은 objstate가 자체 보관
/// (PackData1 — last_known_snapshot)하므로 정확성 불변.
/// v2(2026-08-06): v1의 Prefix 선전진이 베이스 ACK 처리를 "not newer"로 스킵시켜
/// last_known_snapshot 패킷 갱신(L330)이 누락 → 다음 델타가 스테일 기준선 대비 전 필드 풀 재전송을
/// 유발했다 (PlayerSyncPacket의 woundViewTargetNetBodyId가 매 사이클 재적용되어 건강 패널 아이콘이
/// 고속 깜빡임/스턱온 — 2026-08-07 실측). v2는 Prefix에서 ACK ID만 캡처하고 Postfix에서
/// 베이스가 처리한 객체(이미 ackId)를 제외한 나머지만 전진한다 — 베이스 패킷 갱신 보존.
/// 진단(임시): ACK 도착 로그(플레이어별 10초 스로틀) — ACK 미도착 시 별도 원인(클라 측).</summary>
[HarmonyPatch]
internal static class CoolSyncAckBaselinePatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    /// <summary>Prefix에서 캡처한 ACK ID — Postfix에서 소비 (시스템 인스턴스별).</summary>
    private static readonly Dictionary<CoolSyncSubSystemForObjects, ushort> _pendingAckId = new();

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_ReceiveAck");
    }

    /// <summary>ACK ID 캡처 — 원본이 다시 읽도록 위치 복원 (무간섭).</summary>
    private static void Prefix(CoolSyncSubSystemForObjects __instance, NetPlayer plr, NetDataReader reader)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || plr == null || reader == null) return;

        int pos = reader.Position;
        reader.Get(out ushort ackId);
        reader.SetPosition(pos);

        _pendingAckId[__instance] = ackId;
    }

    /// <summary>베이스 ACK 처리(스냅샷 내 객체의 패킷/기준선 갱신) 이후,
    /// 그 외 객체(베이스가 처리하지 않은 쿨 객체)의 기준선 ID만 전진.</summary>
    private static void Postfix(CoolSyncSubSystemForObjects __instance, NetPlayer plr)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || plr == null) return;
        if (!_pendingAckId.TryGetValue(__instance, out ushort ackId)) return;
        _pendingAckId.Remove(__instance);

        if (PerPlrStatesField?.GetValue(__instance) is not IDictionary states) return;
        if (!states.Contains(plr.clientId)) return;
        var state = states[plr.clientId] as CoolSyncSubSystemForObjects.Server_PerPlrState;
        if (state == null) return;

        // 근본 수정 — 기준선 ID 일괄 전진 (델타 기준 패킷은 유지 — 정확성 불변).
        // 베이스가 방금 ackId로 올린 객체는 비교(더 오래된 경우만)에서 자동 제외된다.
        foreach (var kv in state.objstates)
        {
            var objState = kv.Value;
            if (objState == null) continue;
            if ((short)(ackId - objState.last_known_snapshot_id) > 0)
            {
                objState.last_known_snapshot_id = ackId;
            }
        }
    }
}
