using System;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// TryGetSyncInfoOrRegister(Component, out SyncInfo) null 가드 — 거래 실패 경로의 NRE 방지.
[HarmonyPatch]
internal static class NetObjectRegistry_TryGetSyncInfoOrRegister_NullGuardPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        // out 파라미터는 ByRef — argTypes로는 바인딩 불가라 TargetMethod로 정확히 지정한다.
        return AccessTools.Method(typeof(NetObjectRegistry), "TryGetSyncInfoOrRegister",
            new[] { typeof(Component), typeof(SyncInfo).MakeByRefType() });
    }

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
