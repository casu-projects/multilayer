using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// 베이스 모드의 클라이언트 패킷 적용 로직은 "소유 바디/컨테이너가 실제로 바뀔 때만"
/// 방관자 클라이언트의 아이템 슬롯을 갱신한다 - 같은 바디 내 슬롯 이동/교환은 소유자가
/// 그대로라 조건에 안 걸려 방관자가 옛 슬롯을 계속 본다. 서버는 실제 drop→재pickup을
/// 수행하지만 (Body.SwapSlots + 네트워크 픽업 핸들러) 같은 틱 안에 일어나 두 동기화
/// 패킷("바디에서 나감"/"새 슬롯 부착")이 하나로 합쳐져 방관자가 중간 상태를 못 본다.
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

        // 위 drop이 이미 "바디에서 나감" 상태의 강제동기화를 큐잉했다 — 라운드로빈 차례를
        // 기다리지 말고 지금 플러시해, 바로 아래의 재부착이 같은 패킷으로 합쳐지지 않게 한다.
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

        // "새 슬롯 부착" 상태도 같은 이유로 즉시 플러시.
        FlushForcedSyncNow();
        return false;
    }

    /// 등록된 모든 동기화 서브시스템의 틱 전송 단계를 즉시 실행한다
    /// (CoolSyncManager의 라운드로빈 대기 대신). NewCoolerObjectPacketWriteReadSystem
    /// (아이템을 실제로 소유하는 시스템)은 내부 타입이라 공개 BaseCoolSyncSubSystem
    /// 참조로만 도달한다.
    private static void FlushForcedSyncNow()
    {
        foreach (BaseCoolSyncSubSystem system in CoolSyncManager.AllSystems)
        {
            system.Server_Update();
        }
    }
}
