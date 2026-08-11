using System;
using HarmonyLib;
using UnityEngine;

namespace CasuMod;

// Body.HoldingItem(int) null 가드 — 바디 교체/파괴 과도기에 slots가 무효하면 NRE가 난다.
[HarmonyPatch(typeof(Body), nameof(Body.HoldingItem), new Type[] { typeof(int) })]
internal static class Body_HoldingItem_NullGuardPatch
{
    private static bool Prefix(Body __instance, int slot)
    {
        if (__instance == null || __instance.slots == null || slot < 0
            || slot >= __instance.slots.Length || __instance.slots[slot] == null)
        {
            return false;
        }
        return true;
    }
}
