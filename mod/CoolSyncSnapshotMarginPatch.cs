using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 스냅샷 큐 백로그 해소 — ACK(Unreliable) 유실 시 스냅샷 큐가 1500+(TOO MUCH SNAPSHOTS)까지
// 쌓이고 2000 하드 트림이 기준선을 파괴해 전 객체가 full-state로 재전송된다.
// BaselineAdvance: ACK를 기다리지 않고 서버가 직접 보낸 과거 스냅샷(cur_delta_roll - margin)으로
// 기준선 전진 (델타 변경 필드는 절대값이라 안전, 유실 필드는 40초 forgotten-checker가 수렴).
// Drain: 매 프레임 큐를 margin 수준까지 소거. margin = 2초치 틱.
[HarmonyPatch]
internal static class CoolSyncSnapshotBaselineAdvancePatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    // 2초치 틱 (SEND_FREQUENCY 기준).
    internal static int MarginTicks(BaseCoolSyncSubSystem sys) =>
        Mathf.Max(8, Mathf.CeilToInt(2f / sys.SEND_FREQUENCY));

    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "PackAndSend");
    }

    private static void Postfix(CoolSyncSubSystemForObjects __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null) return;

        if (PerPlrStatesField?.GetValue(__instance) is not IDictionary states) return;
        if (states.Count == 0) return;

        int margin = MarginTicks(__instance);
        foreach (DictionaryEntry entry in states)
        {
            if (entry.Value is not CoolSyncSubSystemForObjects.Server_PerPlrState state) continue;
            if (state.snapshots.Count == 0) continue;

            ushort target = (ushort)(state.cur_delta_roll - margin);
            if (!state.snapshots.TryGetValue(target, out var snap)) continue;
            var contains = snap.objects_it_contains;
            if (contains == null || contains.Count == 0) continue;

            foreach (var kv in state.objstates)
            {
                var objState = kv.Value;
                if (objState == null) continue;
                if ((short)(target - objState.last_known_snapshot_id) <= 0) continue;
                if (!contains.TryGetValue(kv.Key, out IDeltaPacketBase pkt) || pkt == null) continue;
                objState.last_known_snapshot = pkt;
                objState.last_known_snapshot_id = target;
            }
        }
    }
}

// 스냅샷 큐 무조건 소거 — ACK(clear_queued)와 무관하게 매 프레임 margin 수준까지 정리.
// ACK 확정 기준선이 최근이면 vanilla 소거가 그 지점에서 자연 정지한다.
[HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Update")]
internal static class CoolSyncSnapshotDrainPatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    private static readonly MethodInfo ClearSnapshotsMethod =
        AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_OneStepClearOldSnapshots");

    private const int MaxDrainPerFrame = 16;
    private const int WarnQueueThreshold = 600;

    // 진단 — ACK 기반 소거가 여전히 실패하는 플레이어 (verbose on에서만, 30초 스로틀).
    private static readonly Dictionary<string, double> _lastWarnAt = new();

    private static void Postfix(CoolSyncSubSystemForObjects __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || ClearSnapshotsMethod == null) return;

        if (PerPlrStatesField?.GetValue(__instance) is not IDictionary states) return;
        if (states.Count == 0) return;

        int margin = CoolSyncSnapshotBaselineAdvancePatch.MarginTicks(__instance);
        foreach (DictionaryEntry entry in states)
        {
            if (entry.Value is not CoolSyncSubSystemForObjects.Server_PerPlrState state) continue;

            int targetSize = margin + 2;
            if (state.snapshot_queue.Count <= targetSize) continue;

            int drained = 0;
            while (state.snapshot_queue.Count > targetSize && drained < MaxDrainPerFrame)
            {
                try
                {
                    if (!(bool)ClearSnapshotsMethod.Invoke(__instance, new object[] { state })) break;
                }
                catch { break; }
                drained++;
            }

            if (Plugin.VerboseLogging && state.snapshot_queue.Count > WarnQueueThreshold)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                string key = $"{__instance.syncsystemid}:{state.plrId}";
                if (_lastWarnAt.TryGetValue(key, out double last) && now - last < 30.0) continue;
                _lastWarnAt[key] = now;
                Plugin.Log.LogWarning(
                    $"[Sync] {__instance.GetType().Name} 큐 {state.snapshot_queue.Count} (margin {margin}) — 플레이어 {state.plrId}");
            }
        }
    }
}
