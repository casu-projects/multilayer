using System;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// 마이그레이션 조용화 게이트 - 동결(FREEZE) 플레이어의 발신을 전면 중단하고, 그 바디/플레이어
// 오브젝트를 타인에게 패킹하지 않는다. 두 가지 레이스를 결정적으로 차단한다:
// 타깃 게이트(동결 플레이어는 로딩 화면 - 출발지 잔존 패킷의 새 월드 오염 차단),
// 주체 게이트(10170으로 제거된 바디가 고스트로 재등장하는 것 차단)
internal static class QuiescencePatches
{
    private static bool IsFrozenTarget(NetPlayer plr) =>
        plr != null && MigrationModule.IsFrozen(plr.GetPersistentId());

    // 주체 게이트 - 오브젝트가 동결 플레이어의 바디(NetBody)/플레이어(NetPlayer)면 스킵
    // CharSync/PlrSync/객체 시스템 공통 (Server_Object.real_obj 리플렉션 조회)
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

    // 타깃 게이트: 동결된 플레이어에게 패킷 금지 (시스템별 3곳)

    // 베이스 (PlrSync 등 오버라이드 없는 시스템)
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

    // CharSync (바디 스트림)
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

    // NewCoolerObjectPacketWriteReadSystem (객체/아이템 스트림)
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

    // 주체 게이트: 동결 플레이어의 오브젝트를 타인에게 패킹 금지 (2곳)

    // CharSync 오버라이드 (바디) - 동결 플레이어의 바디를 스킵
    // (객체 시스템은 SyncInfo가 real_obj라 주체 판정이 통과 - 아이템은 FREEZE에서
    // 이미 파괴되므로 불필요. PlrSync는 베이스 패치가 커버)
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

    // 베이스 (PlrSync - 플레이어 위치/방향 오브젝트)
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
