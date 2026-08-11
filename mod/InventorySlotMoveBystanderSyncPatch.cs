using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 같은 바디 내 슬롯 이동/교환 시 방관자 클라이언트의 슬롯이 갱신되지 않는 문제 해결 —
// drop→재pickup(0.15초 창) 시 강제동기화를 즉시 플러시해 두 상태를 분리된 패킷으로 보낸다.
[HarmonyPatch(typeof(Body), "DropItem", new Type[] { typeof(Item) })]
internal static class Body_DropItem_RecordForBystanderSlotSyncPatch
{
    private const float StaleEntryPurgeSeconds = 5f;

    internal static readonly Dictionary<Item, (Body Body, float DroppedAt)> RecentDrops = new();

    private static void Postfix(Body __instance, Item item)
    {
        if (!KrokoshaScavMultiplayer.is_server || item == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (RecentDrops.Count > 0)
        {
            var stale = new List<Item>();
            foreach (KeyValuePair<Item, (Body Body, float DroppedAt)> kv in RecentDrops)
            {
                if (now - kv.Value.DroppedAt > StaleEntryPurgeSeconds)
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (Item staleItem in stale)
            {
                RecentDrops.Remove(staleItem);
            }
        }

        RecentDrops[item] = (__instance, now);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
internal static class Body_PickUpItem_ForceBystanderSlotSyncPatch
{
    private const float SameBodyReslotWindowSeconds = 0.15f;
    private static readonly HashSet<Item> InFlightItems = new HashSet<Item>();

    private static bool Prefix(Body __instance, Item item, int slot)
    {
        if (!KrokoshaScavMultiplayer.is_server || item == null || InFlightItems.Contains(item))
        {
            return true;
        }

        if (!Body_DropItem_RecordForBystanderSlotSyncPatch.RecentDrops.TryGetValue(item, out (Body Body, float DroppedAt) record))
        {
            return true;
        }
        Body_DropItem_RecordForBystanderSlotSyncPatch.RecentDrops.Remove(item);

        if (record.Body != __instance || Time.realtimeSinceStartup - record.DroppedAt > SameBodyReslotWindowSeconds)
        {
            return true;
        }

        // drop이 큐잉한 "바디에서 나감" 강제동기화를 즉시 플러시 — 재부착이 같은 패킷으로
        // 합쳐지지 않게 한다.
        FlushForcedSyncNow();

        item.rb.simulated = false;
        item.rb.velocity = Vector2.zero;

        InFlightItems.Add(item);
        try
        {
            __instance.PickUpItem(item, slot, force: true);
        }
        finally
        {
            InFlightItems.Remove(item);
        }

        // "새 슬롯 부착"도 동일하게 즉시 플러시.
        FlushForcedSyncNow();
        return false;
    }

    // 모든 동기화 서브시스템의 틱 전송을 즉시 실행 (라운드로빈 대기 제거).
    private static void FlushForcedSyncNow()
    {
        foreach (BaseCoolSyncSubSystem system in CoolSyncManager.AllSystems)
        {
            system.Server_Update();
        }
    }
}
