using System;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>NetObjectRegistry.TryGetSyncInfoOrRegister(Component) null 가드.
/// TraderScript_TryPurchase_MultiplayerPatch.Server_Trader_TryPurchase(10163)가
/// 구매 실패(null 반환) 시 무조건 이 메서드를 호출해 NRE를 유발한다 —
/// 모드 저자는 GetSyncInfo(Component)에는 null 가드를 두었으나 이 오버로드에는 빠뜨렸다.
/// obj==null/파괴된 컴포넌트면 스킵하고 false 반환 (기존: NRE 발생). 정상 객체는 원본 그대로.</summary>
[HarmonyPatch(typeof(NetObjectRegistry), nameof(NetObjectRegistry.TryGetSyncInfoOrRegister), new Type[] { typeof(Component) })]
internal static class NetObjectRegistry_TryGetSyncInfoOrRegister_NullGuardPatch
{
    private static bool Prefix(Component obj, ref SyncInfo si)
    {
        if (obj == null)
        {
            si = null;
            return false;
        }
        return true;
    }
}