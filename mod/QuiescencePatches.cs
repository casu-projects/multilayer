using System;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

/// <summary>마이그레이션 조용화 게이트 (P3) — FREEZE(동결)된 플레이어에 대한 발신을 전면
/// 중단하고, 동결된 플레이어의 바디/플레이어 오브젝트를 타인에게 패킹하지 않는다.
///
/// 두 가지 레이스를 결정적으로 차단한다:
/// ① 타깃 게이트 (ShouldPackPacketFor): 동결된 플레이어는 로딩 화면에 있으므로 어떤
///    월드 상태도 수신해서는 안 된다 (출발지 잔존 패킷으로 새 월드가 오염되는 문제).
/// ② 주체 게이트 (PackObjectForPlr): 10170으로 타인의 화면에서 이미 제거된 동결
///    플레이어의 바디가 CharSync/PlrSync 패킷으로 "고스트 재등장"하는 것을 방지한다.
///    (클라이언트는 신원 없는 바디를 NPC로 재생성하고, PlrSync는 10024 요청을 재발신해
///    신원이 재확립되는 악순환이 생긴다)
internal static class QuiescencePatches
{
    private static bool IsFrozenTarget(NetPlayer plr) =>
        plr != null && MigrationModule.IsFrozen(plr.GetPersistentId());

    /// <summary>주체 게이트 — 오브젝트가 동결 플레이어의 바디(NetBody)/플레이어(NetPlayer)면 스킵.
    /// CharSync/PlrSync/객체 시스템 공통 (Server_Object.real_obj 리플렉션 조회).</summary>
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

    // ── 타깃 게이트: 동결된 플레이어에게 패킷 금지 (시스템별 3곳) ──

    /// <summary>베이스 (PlrSync 등 오버라이드 없는 시스템).</summary>
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

    /// <summary>CharSync (바디 스트림).</summary>
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

    /// <summary>NewCoolerObjectPacketWriteReadSystem (객체/아이템 스트림).</summary>
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

    // ── 주체 게이트: 동결 플레이어의 오브젝트를 타인에게 패킹 금지 (2곳) ──

    /// <summary>CharSync 오버라이드 (바디) — 동결 플레이어의 바디를 스킵.
    /// (객체 시스템은 SyncInfo가 real_obj라 주체 판정이 통과 — 아이템은 FREEZE에서
    ///  이미 파괴되므로 불필요. PlrSync는 베이스 패치가 커버)</summary>
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

    /// <summary>베이스 (PlrSync — 플레이어 위치/방향 오브젝트).</summary>
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
