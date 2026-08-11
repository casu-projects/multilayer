using System;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 마이그레이션 조용화 게이트 — 동결(FREEZE)된 플레이어에게 패킷을 보내지 않고(타깃 게이트),
// 동결 플레이어의 바디/플레이어 오브젝트를 타인에게 패킹하지 않는다(주체 게이트).
internal static class QuiescencePatches
{
    private static bool IsFrozenTarget(NetPlayer plr) =>
        plr != null && MigrationModule.IsFrozen(plr.GetPersistentId());

    // 주체 게이트 — 오브젝트가 동결 플레이어의 바디/플레이어면 스킵.
    private static bool ShouldSkipSubject(object obj)
    {
        if (obj == null) return false;
        object realObj = obj.GetType().GetField("real_obj")?.GetValue(obj);
        if (realObj == null) return false;

        if (realObj is NetBody netBody && netBody.is_player && netBody.plr != null
            && MigrationModule.IsFrozen(netBody.plr.GetPersistentId()))
        {
            return true;
        }
        if (realObj is NetPlayer netPlayer && MigrationModule.IsFrozen(netPlayer.GetPersistentId()))
        {
            return true;
        }
        return false;
    }

    // 타깃 게이트 — 시스템별 3곳.

    [HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "ShouldPackPacketFor")]
    internal static class ShouldPackPacketFor_Base_FrozenTargetPatch
    {
        private static bool Prefix(NetPlayer plr, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
            if (IsFrozenTarget(plr))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class ShouldPackPacketFor_CharSync_FrozenTargetPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                AccessTools.TypeByName("KrokoshaCasualtiesMP.CharSync"), "ShouldPackPacketFor");

        private static bool Prefix(NetPlayer plr, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
            if (IsFrozenTarget(plr))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class ShouldPackPacketFor_Objects_FrozenTargetPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                AccessTools.TypeByName("KrokoshaCasualtiesMP.NewCoolerObjectPacketWriteReadSystem"),
                "ShouldPackPacketFor");

        private static bool Prefix(NetPlayer plr, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
            if (IsFrozenTarget(plr))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // 주체 게이트 — 2곳.

    [HarmonyPatch]
    internal static class PackObjectForPlr_CharSync_FrozenSubjectPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                AccessTools.TypeByName("KrokoshaCasualtiesMP.CharSync"), "PackObjectForPlr");

        private static bool Prefix(object obj, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
            if (ShouldSkipSubject(obj))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CoolSyncSubSystemForObjects), "PackObjectForPlr")]
    internal static class PackObjectForPlr_Base_FrozenSubjectPatch
    {
        private static bool Prefix(object obj, ref bool __result)
        {
            if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
            if (ShouldSkipSubject(obj))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
