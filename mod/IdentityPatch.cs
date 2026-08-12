using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

// tail v2 (/): connect data 끝 15바이트 [Magic C5A5][Ver 1][SteamID64 8B]
// [clientId 2B][isReturning 1B][isMigratingArrival 1B]. Magic/Ver 불일치 시 접속 거부 (fail-fast).
[HarmonyPatch]
internal static class OnConnectionRequest_TailV2Patch
{
    private const byte MagicHigh = 0xC5;
    private const byte MagicLow = 0xA5;
    private const byte Version = 1;
    private const int TailSize = 15;

    private static ulong? _pendingSteamId;
    private static ushort? _pendingClientId;
    private static bool? _pendingIsReturning;
    private static bool? _pendingIsMigratingArrival;

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TransportLiteNetLib), "LiteNetLib.INetEventListener.OnConnectionRequest");

    private static void Prefix(ConnectionRequest request, ref bool __runOriginal)
    {
        _pendingSteamId = null;
        _pendingClientId = null;
        _pendingIsReturning = null;
        _pendingIsMigratingArrival = null;

        byte[] raw = request.Data.RawData;
        int offset = request.Data.UserDataOffset;
        int size = request.Data.UserDataSize;
        if (size < TailSize)
            return;

        int start = offset + size - TailSize;
        if (raw[start] != MagicHigh || raw[start + 1] != MagicLow || raw[start + 2] != Version)
        {
 // 버전 스큐 - 조용한 실패 금지 ( fail-fast)
            Plugin.Log.LogError($"[Identity] tail v2 불일치 (Magic/Ver) — 접속 거부. "
                + "게이트웨이와 모드 버전을 맞춰야 합니다.");
            var reject = new NetDataWriter();
            reject.Put("Version mismatch: gateway/mod protocol skew.");
            request.Reject(reject);
            __runOriginal = false;
            return;
        }

        var r = new NetDataReader(raw, start + 3, size - 3);
        _pendingSteamId = r.GetULong();
        _pendingClientId = r.GetUShort();
        _pendingIsReturning = r.GetBool();
        _pendingIsMigratingArrival = r.GetBool();
    }

    internal static ulong? TakeSteamId() { var v = _pendingSteamId; _pendingSteamId = null; return v; }
    internal static ushort? TakeClientId() { var v = _pendingClientId; _pendingClientId = null; return v; }
    internal static bool? TakeIsMigrating() { var v = _pendingIsMigratingArrival; _pendingIsMigratingArrival = null; return v; }
    internal static bool TakeIsReturning() { var v = _pendingIsReturning ?? false; _pendingIsReturning = null; return v; }
}

// 오케스트레이터가 전역 배정한 clientId 강제 적용.
[HarmonyPatch(typeof(Net), "GetNextPlayerId")]
internal static class Net_GetNextPlayerId_ForceClientIdPatch
{
    private static bool Prefix(ref knetid __result)
    {
        ushort? forced = OnConnectionRequest_TailV2Patch.TakeClientId();
        if (!forced.HasValue || forced.Value == 0)
            return true;
        if (NetPlayer.ClientIdToPlayerDict.ContainsKey(forced.Value))
            return true;
        __result = forced.Value;
        return false;
    }
}

// 복귀(재접속/세션 복원) 플레이어 clientId 집합 - RosterBarrier가 신뢰성 DELETE로
// 관찰자들의 NPC/스테일 항목을 정리해 재생성하게 하는 데 사용 .
internal static class ReturningTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

// 마이그레이션 도착 플레이어 clientId 집합 - 접속 공지 억제 전용
// (isMigratingArrival=true - 게이트웨이 SwapBackend만 true, 일반 재접속은 미등록).
// 마이그레이션 도착만 억제하고 퇴장 후 재접속은 접속 공지를 표시하기 위한 구분 .
internal static class MigrationArrivalTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

// 신원 적용: SteamID64 + 접속 시 PLAYER_DATA_REQUEST 전송.
[HarmonyPatch(typeof(TransportLiteNetLib), nameof(TransportLiteNetLib.CreateNetPlayerWithPeer))]
internal static class CreateNetPlayerWithPeer_ApplyIdentityPatch
{
    private static void Postfix(NetPlayer __result)
    {
        if (__result == null || !KrokoshaScavMultiplayer.is_dedicated_server)
            return;

        ulong? steamId = OnConnectionRequest_TailV2Patch.TakeSteamId();
        bool isMigrating = OnConnectionRequest_TailV2Patch.TakeIsMigrating() ?? false;
        bool isReturning = OnConnectionRequest_TailV2Patch.TakeIsReturning();
        if (isReturning || isMigrating)
        {
            ReturningTracker.ClientIds.Add(__result.clientId);
        }
        if (isMigrating)
        {
            MigrationArrivalTracker.ClientIds.Add(__result.clientId);
        }

        if (steamId.HasValue && steamId.Value != 0)
        {
            __result.steam_id = steamId.Value;
        }

 // 도착 epoch: 직전 방문의 잔존 PendingData/PendingPositions 소비 차단.
 // 바디 스폰(Body.Start)보다 반드시 먼저 실행된다 (CreateNetPlayerWithPeer 동기 처리).
        SaveModule.OnPlayerArrival(__result.GetPersistentId());

 // : 접속 시 플레이어 데이터 요청 - 직접연결(steam_id=0) 포함 전 플레이어.
 // playerKey는 GetPersistentId (STEAM_ 경로 패치 적용됨 - steam_id 있으면 STEAM_,
 // 없으면 NAME_hex(username) - 게이트웨이 키와 일치).
        if (OrchestratorClient.Instance != null)
        {
            OrchestratorClient.Instance.SendEvent("PLAYER_DATA_REQUEST",
                new { playerKey = __result.GetPersistentId() });
        }
    }
}

// GetPersistentId - steam_id가 있으면 STEAM_ 경로 (게이트웨이 키 일치).
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.GetPersistentId))]
internal static class NetPlayer_GetPersistentId_PreferSteamIdPatch
{
    private static bool Prefix(NetPlayer __instance, ref string __result)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || __instance.steam_id == 0)
            return true;
        __result = "STEAM_" + __instance.steam_id;
        return false;
    }
}
