using System;
using HarmonyLib;
using UnityEngine;

namespace CasuMod;

// Body.HoldingItem(int) null 가드.
// Talker.impairedSpeech -> HoldingItem(2) 경로에서 발신자 바디가 교체/파괴 과도기에
// slots 배열(또는 slots[slot])이 null이면 NRE -> 채팅 10099 처리 유실.
// slots 무효 시 false(들고 있지 않음) 반환 - 모든 호출부에 보수적·안전.
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
