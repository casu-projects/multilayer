using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>바닐라/베이스 모드 안정성 가드 이식 (P3 — 구 모드 project/mod 이식).
/// NetPlayer.GetDistanceToNearestLivingPlayer NRE — 퇴장 후 sync 목록에 잔존하는
/// null-body 항목으로 인한 폭풍을 차단 (퇴장 직후 NRE 폭풍의 원인).</summary>
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

/// <summary>real_obj가 사라진 스테일 server_objects를 정리하고 클라이언트에 삭제를 알림
/// 스냅샷은 여기서 직접 지우지 않음. ACK 기준선까지 지웠기 때문에 안정성 가드 부분을 지울 수 없기 때문이에요.</summary>
[HarmonyPatch]
internal static class CoolSyncStaleObjectGuardPatch
{
    private static FieldInfo _statesField;
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

        var objstatesField = _objstatesField;

        // server_objects에서 실체가 사라진 항목만 정리하게 만들고, 스냅샷 큐는 KrokMP의 정상 경로에 맡기게 함.
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
            // 1. 정상 할당 해제 흐름과 비슷하게 서버 객체 목록과 플레이어별 객체 상태에서 제거하게 만듬.
            serverObjs.Remove(netId);
            foreach (DictionaryEntry kv in states)
            {
                var objstates = objstatesField?.GetValue(kv.Value) as IDictionary;
                objstates?.Remove(netId);
            }

            // 2. 이미 사라진 객체가 검색되지 않도록 네트워크 ID와 동기화 레지스트리도 정리.
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

            // 3. 모든 플레이어의 강제 동기화 큐에 넣어 각각의 클라이언트에도 객체가 사라졌음을 알림.
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
        _objstatesField = AccessTools.Field(plrStateType, "objstates");
        _serverObjectsField = AccessTools.Field(ownerType, "server_objects");
        _realObjField = AccessTools.Field(
            AccessTools.Inner(ownerType, "Server_Object"), "real_obj");
    }
}