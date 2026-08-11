using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>CoolSync 스냅샷 백로그 근본 해소 — ACK 무의존 (2026-08-11).
/// 문제: 스냅샷 큐 소거가 클라→서버 ACK(Unreliable) 수신 시에만 진행되고 기준선도 ACK로만
/// 전진하므로, ACK 유실/지연 시 ① 큐가 1500+(TOO MUCH SNAPSHOTS 경고)까지 쌓이고
/// ② 2000 상한 하드 트림이 기준선을 파괴해 전 객체가 매 틱 full-state 재전송을 유발한다
/// (객체 스트림 대역폭 ~10배 폭증 — 다인원 롤백의 증폭기).
///
/// 해결 ① BaselineAdvance: ACK를 기다리지 않고 서버가 직접 보낸 과거 스냅샷
/// (cur_delta_roll - margin)으로 기준선을 매 패킹 사이클에 전진. 델타의 변경 필드는 절대값이므로
/// 클라 상태와 기준선이 살짝 어긋나도 변경 필드는 정확히 적용되고, 유실된 필드는 기존 40초
/// forgotten-checker → 정보 재요청 → full 재동기화 경로가 수렴한다.
///
/// 해결 ② Drain: clear_queued(ACK)를 기다리지 않고 매 프레임 큐를 margin 수준까지 소거 —
/// 경고 소멸 + 하드 트림 불가 + 메모리/대역폭 안정. ACK 확정 기준선이 margin보다 최근이면
/// vanilla 소거가 그 지점에서 자연 정지한다 (의미상 정확).
///
/// 기존 CoolSyncAckBaselinePatch(ACK 확정 전진)와 공존 — margin 전진이 바닥 역할.
/// margin = 2초치 틱 (객체 16 / CharSync 60 / PlrSync 20).</summary>
[HarmonyPatch]
internal static class CoolSyncSnapshotBaselineAdvancePatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    /// <summary>2초치 틱 (SEND_FREQUENCY 기준 — 객체 0.125s, CharSync 1/30s, PlrSync 0.1s).</summary>
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

/// <summary>스냅샷 큐 무조건 소거 (Drain) — ACK(clear_queued)와 무관하게 매 프레임
/// margin 수준까지 정리. BaselineAdvance가 기준선을 margin으로 전진하므로 큐 앞부분은
/// pinning이 없어 안전하게 소거된다. ACK 확정 기준선이 최근이면 vanilla 소거가 그 지점에서
/// 정지하고, 큐는 그 크기로 유지된다 (ACK가 정상이면 더 작음).</summary>
[HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Update")]
internal static class CoolSyncSnapshotDrainPatch
{
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");

    private static readonly MethodInfo ClearSnapshotsMethod =
        AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_OneStepClearOldSnapshots");

    private const int MaxDrainPerFrame = 16;
    private const int WarnQueueThreshold = 600;

    /// <summary>진단 — ACK 기반 소거가 여전히 실패하는 플레이어 (verbose on에서만, 30초 스로틀).</summary>
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
