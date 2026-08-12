using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 같은 캐릭터 안에서 아이템 슬롯 이동이 다른 유저 화면에도 보이도록:
// KrokMP는 같은 틱의 drop과 pickup을 마지막 상태 하나로 합칠 수 있다. 모든 CoolSync 시스템을
// 강제 갱신하면 무관한 아이템까지 패킷이 늘어나므로, 실제 슬롯 교환이 쓰는 DropItem(int)만
// 기록하고 같은 몸으로 돌아오는 pickup을 다음 서버 틱으로 미뤄 두 상태를 분리한다
internal static class InventorySlotMoveBystanderSyncPatch
{
    private const float SameBodyMoveWindowSeconds = 0.15f;
    private const float DeferredPickupSeconds = 0.12f;
    private const int CleanupThreshold = 256;

    private sealed class RecentDrop
    {
        internal Body Body;
        internal float Time;
    }

    private static readonly Dictionary<int, RecentDrop> RecentDrops = new();
    private static readonly HashSet<int> ReplayingPickups = new();

    [HarmonyPatch(typeof(Body), nameof(Body.DropItem), new[] { typeof(int) })]
    private static class BodyDropItemRecordPatch
    {
        private static void Prefix(Body __instance, int __0, out Item __state)
        {
            __state = null;
            if (!ShouldRun() || __instance == null) return;

            __state = __instance.GetItem(__0);
        }

        private static void Postfix(Body __instance, Item __state)
        {
            if (!ShouldRun() || __instance == null || __state == null) return;

            int itemId = __state.GetInstanceID();
            RecentDrops[itemId] = new RecentDrop
            {
                Body = __instance,
                Time = Time.realtimeSinceStartup
            };

            if (RecentDrops.Count > CleanupThreshold)
            {
                CleanupExpiredDrops(Time.realtimeSinceStartup);
            }
        }
    }

    [HarmonyPatch(typeof(Body), nameof(Body.PickUpItem), new[] { typeof(Item), typeof(int), typeof(bool) })]
    private static class BodyPickUpItemDeferPatch
    {
        private static bool Prefix(Body __instance, Item __0, int __1, bool __2)
        {
            if (!ShouldRun() || __instance == null || __0 == null) return true;

            int itemId = __0.GetInstanceID();
            if (ReplayingPickups.Contains(itemId)) return true;
            if (!RecentDrops.TryGetValue(itemId, out RecentDrop drop)) return true;

            RecentDrops.Remove(itemId);
            float age = Time.realtimeSinceStartup - drop.Time;
            if (drop.Body != __instance || age < 0f || age > SameBodyMoveWindowSeconds)
            {
                return true;
            }

            Body body = __instance;
            Item item = __0;
            int slot = __1;
            bool pickupFlag = __2;

            // 전체 동기화를 강제로 돌리지 않고, drop을 서버에 먼저 전송될 시간을 조금 준다
            KrokoshaCasualtiesUtils.Util.DelayCallLambda(DeferredPickupSeconds, (Action)(() =>
            {
                if (body == null || item == null) return;

                int delayedItemId = item.GetInstanceID();
                ReplayingPickups.Add(delayedItemId);
                try
                {
                    body.PickUpItem(item, slot, pickupFlag);
                }
                finally
                {
                    ReplayingPickups.Remove(delayedItemId);
                }
            }));

            return false;
        }
    }

    private static bool ShouldRun() =>
        KrokoshaScavMultiplayer.network_system_is_running
        && KrokoshaScavMultiplayer.is_server;

    private static void CleanupExpiredDrops(float now)
    {
        var expired = new List<int>();
        foreach (KeyValuePair<int, RecentDrop> pair in RecentDrops)
        {
            if (pair.Value.Body == null || now - pair.Value.Time > SameBodyMoveWindowSeconds)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (int itemId in expired)
        {
            RecentDrops.Remove(itemId);
        }
    }
}
