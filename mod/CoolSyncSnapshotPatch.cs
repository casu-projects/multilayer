using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>CoolSync 스냅샷 백로그 제어 (2026-08-03) — "TOO MUCH SNAPSHOTS!!!" 경고/메모리 개선.
/// 델타 동기화의 스냅샷 히스토리는 클라이언트 ACK(10175, Unreliable)가 유일한 정리 트리거라,
/// ACK 유실 시 백로그가 누적되고 (상한 2000, 경고 1500) 지속 경고가 발생한다.
/// A. 스냅샷 상한 축소 (2000 → 600 — 베이스 상태는 객체에 보관되므로 히스토리만 줄어 안전).
/// B. ACK-무관 주기적 정리 — 큐가 임계(300)를 넘으면 clear_queued와 무관하게, 베이스가
///    이미 지나간 스냅샷만 제거하는 바닐라 정리 루틴을 틱당 일부 실행한다 (안전 범위만).</summary>
[HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "Update")]
internal static class CoolSync_SnapshotPrunePatch
{
    /// <summary>강제 반감 상한 (A) — 바닐라 2000 → 600.</summary>
    private const int MaxSnapshotQueue = 600;

    /// <summary>ACK-무관 정리 임계 (B) — 큐가 이 값을 넘으면 정리 루프 실행.</summary>
    private const int PruneThreshold = 300;

    /// <summary>틱당 최대 정리 수 (B) — 롤 생성 30Hz 대비 넉넉히.</summary>
    private const int PrunePerTick = 4;

    private static readonly FieldInfo MaxSnapshotQueueField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "max_snapshot_queue");
    private static readonly FieldInfo PerPlrStatesField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects), "server_perplrstates");
    private static readonly FieldInfo SnapshotQueueField =
        AccessTools.Field(typeof(CoolSyncSubSystemForObjects.Server_PerPlrState), "snapshot_queue");
    private static readonly MethodInfo PruneMethod =
        AccessTools.Method(typeof(CoolSyncSubSystemForObjects), "Server_OneStepClearOldSnapshots");

    private static void Postfix(CoolSyncSubSystemForObjects __instance)
    {
        if (Net.is_client || !KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null) return;

        // A — 상한 축소 (시스템 인스턴스별 1회 — 2000이면 600으로).
        if (MaxSnapshotQueueField != null
            && MaxSnapshotQueueField.GetValue(__instance) is int cur && cur == 2000)
        {
            MaxSnapshotQueueField.SetValue(__instance, MaxSnapshotQueue);
        }

        // B — ACK-무관 주기적 정리 (베이스가 지나간 스냅샷만 — 안전).
        if (PerPlrStatesField?.GetValue(__instance) is not IDictionary perPlrStates)
            return;

        foreach (DictionaryEntry entry in perPlrStates)
        {
            if (entry.Value == null) continue;
            if (SnapshotQueueField?.GetValue(entry.Value) is not Queue<ushort> queue)
                continue;
            if (queue.Count <= PruneThreshold) continue;

            for (int i = 0; i < PrunePerTick && queue.Count > PruneThreshold; i++)
            {
                if (PruneMethod?.Invoke(__instance, new[] { entry.Value }) is false)
                    break; // 차단됨 (베이스가 가장 오래된 스냅샷에 물림) — 이번 틱 중단
            }
        }
    }
}
