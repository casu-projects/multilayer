using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod;

// ACK 도착 시 모든 객체의 델타 기준선 ID를 ACK된 스냅샷으로 전진 — 스냅샷 큐 정리가
// "쿨" 객체의 옛 기준선에 블록돼 1500+까지 쌓이는 문제(TOO MUCH SNAPSHOTS)를 해결한다.
// Prefix에서 ACK ID만 캡처하고 Postfix에서 베이스 처리 이후 전진한다 (베이스 패킷 갱신 보존).
[HarmonyPatch]
internal static class CoolSyncAckBaselinePatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    // Prefix에서 캡처한 ACK ID — Postfix에서 소비.
    private static readonly Dictionary<CoolSyncSubSystemForObjects, ushort> _pendingAckId = new();

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_ReceiveAck");
    }

    private static void Prefix(CoolSyncSubSystemForObjects __instance, NetPlayer plr, NetDataReader reader)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || plr == null || reader == null) return;

        int pos = reader.Position;
        reader.Get(out ushort ackId);
        reader.SetPosition(pos);

        _pendingAckId[__instance] = ackId;
    }

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
