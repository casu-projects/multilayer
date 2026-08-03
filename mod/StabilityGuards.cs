using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>바닐라/베이스 모드 안정성 가드 이식 (P3 — 구 모드 project/mod 이식):
/// ① NetPlayer.GetDistanceToNearestLivingPlayer NRE — 퇴장 후 sync 목록에 잔존하는
///    null-body 항목으로 인한 폭풍을 차단 (퇴장 직후 NRE 폭풍의 원인).
/// ② CoolSync 스냅샷 큐 백로그 — "TOO MUCH SNAPSHOTS" 폭주/델타 기준선 손실 방지.
/// <summary>AllLivingPlayers에 Unity 지연 Destroy~OnDestroy 사이 잔존하는 null-body 항목이
/// 있을 때 폭발하는 NRE를 차단한다. 메서드를 대체해 null/destroyed 항목을 건너뛴다.</summary>
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.GetDistanceToNearestLivingPlayer))]
internal static class NetPlayer_GetDistanceToNearestLivingPlayer_NullGuardPatch
{
    private static bool Prefix(Vector2 target, ref (NetPlayer, float) __result)
    {
        float best = float.PositiveInfinity;
        NetPlayer nearest = null;

        foreach (NetPlayer candidate in NetPlayer.AllLivingPlayers)
        {
            if (candidate == null || candidate.body == null)
            {
                continue;
            }

            float distSqr = KM.dist2dsqr((Vector2)candidate.body.transform.position, in target);
            if (distSqr < best)
            {
                best = distSqr;
                nearest = candidate;
            }
        }

        __result = (nearest, best);
        return false;
    }
}

/// <summary>스냅샷 큐 백로그 가드: ① 2000 초과 시 50% 정리 (베이스와 동일 규칙, 강제 실행),
/// ② real_obj가 사라진 스테일 server_objects 제거 + 강제 삭제 큐잉 — ACK 유실로
/// 백로그가 쌓여 "TOO MUCH SNAPSHOTS"가 터지고 델타 기준선이 손실되는 경로를 차단.</summary>
[HarmonyPatch]
internal static class CoolSyncSnapshotGuardPatch
{
    private const int MaxSnapshotsPerPlayer = 2000;

    private static FieldInfo _statesField;
    private static FieldInfo _snapshotsField;
    private static FieldInfo _queueField;
    private static FieldInfo _objstatesField;
    private static FieldInfo _serverObjectsField;
    private static FieldInfo _realObjField;

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_Update");
    }

    private static void Prefix(CoolSyncSubSystemForObjects __instance)
    {
        CacheFields(__instance.GetType());

        var states = _statesField?.GetValue(__instance) as IDictionary;
        if (states == null) return;

        var snapshotsField = _snapshotsField;
        var queueField = _queueField;
        var objstatesField = _objstatesField;
        if (snapshotsField == null || queueField == null) return;

        // ── 1. Snapshot queue overflow guard (trim 50%, matching KrokoshaMP) ──
        List<object> toTrim = null;
        foreach (DictionaryEntry kv in states)
        {
            var snapshots = snapshotsField.GetValue(kv.Value) as IDictionary;
            if (snapshots != null && snapshots.Count > MaxSnapshotsPerPlayer)
            {
                (toTrim ??= new List<object>()).Add(kv.Key);
            }
        }

        if (toTrim != null)
        {
            foreach (var id in toTrim)
            {
                if (!states.Contains(id)) continue;
                var state = states[id];
                var snapshots = snapshotsField.GetValue(state) as IDictionary;
                var queue = queueField.GetValue(state) as IEnumerable;
                if (snapshots == null || queue == null) continue;

                var dequeueMethod = queue.GetType().GetMethod("Dequeue");
                if (dequeueMethod == null) continue;

                int removeCount = Mathf.CeilToInt(MaxSnapshotsPerPlayer * 0.5f);
                for (int i = 0; i < removeCount; i++)
                {
                    var key = dequeueMethod.Invoke(queue, null);
                    snapshots.Remove(key);
                }
            }
        }

        // ── 2. server_objects: remove stale objects ──
        var serverObjs = _serverObjectsField?.GetValue(__instance) as IDictionary;
        if (serverObjs == null || _realObjField == null) return;

        List<object> staleNetIds = null;
        foreach (DictionaryEntry objEntry in serverObjs)
        {
            var serverObj = objEntry.Value;
            var realObj = _realObjField.GetValue(serverObj);

            if (realObj == null)
            {
                (staleNetIds ??= new List<object>()).Add(objEntry.Key);
                continue;
            }

            if (realObj is UnityEngine.Object unityObj && unityObj == null)
            {
                (staleNetIds ??= new List<object>()).Add(objEntry.Key);
            }
        }

        if (staleNetIds == null) return;

        foreach (var netId in staleNetIds)
        {
            // 1. Remove from server_objects + objstates (like Server_Internal_DeallocateObject).
            serverObjs.Remove(netId);
            foreach (DictionaryEntry kv in states)
            {
                var objstates = objstatesField?.GetValue(kv.Value) as IDictionary;
                objstates?.Remove(netId);
            }

            // 2. Clean up NetIdToSyncInfoDict + SyncRegistry.
            var netIdDict = AccessTools.Field(typeof(NetObjectRegistry),
                "NetIdToSyncInfoDict")?.GetValue(null) as IDictionary;
            if (netIdDict?.Contains(netId) == true)
            {
                var syncInfo = netIdDict[netId];
                var goField = AccessTools.Field(syncInfo.GetType(), "go");
                var go = goField?.GetValue(syncInfo);
                if (go != null)
                {
                    var syncRegistry = AccessTools.Field(typeof(NetObjectRegistry),
                        "SyncRegistry")?.GetValue(null) as IDictionary;
                    syncRegistry?.Remove(go);
                }
                netIdDict.Remove(netId);
            }

            // 3. Queue force-sync to all players (tells clients this object is gone).
            foreach (DictionaryEntry kv in states)
            {
                var state = kv.Value;
                var forcesyncQueueField = state.GetType().GetField("forcesync_queue");
                var queue = forcesyncQueueField?.GetValue(state) as IEnumerable;
                if (queue == null) continue;

                var containsMethod = queue.GetType().GetMethod("Contains");
                var enqueueMethod = queue.GetType().GetMethod("Enqueue");
                if (containsMethod == null || enqueueMethod == null) continue;

                var contains = containsMethod.Invoke(queue, new[] { netId });
                if (contains is bool b && !b)
                    enqueueMethod.Invoke(queue, new[] { netId });
            }
        }
    }

    private static void CacheFields(Type ownerType)
    {
        if (_statesField != null) return;

        _statesField = AccessTools.Field(ownerType, "server_perplrstates");
        var plrStateType = AccessTools.Inner(ownerType, "Server_PerPlrState");
        _snapshotsField = AccessTools.Field(plrStateType, "snapshots");
        _queueField = AccessTools.Field(plrStateType, "snapshot_queue");
        _objstatesField = AccessTools.Field(plrStateType, "objstates");
        _serverObjectsField = AccessTools.Field(ownerType, "server_objects");
        _realObjField = AccessTools.Field(
            AccessTools.Inner(ownerType, "Server_Object"), "real_obj");
    }
}
