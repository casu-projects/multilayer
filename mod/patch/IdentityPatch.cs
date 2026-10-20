using CasuMp.Protocol;
using CasuMod;
using System;
using System.Reflection;
using HarmonyLib;
using Together;
using LiteNetLib;

namespace CasuMod.Patch;

[HarmonyPatch]
internal static class OnConnectionRequest_TailV2Patch
{
    private static ulong? _pendingSteamId;
    private static ushort? _pendingClientId;
    private static bool? _pendingIsReturning;
    private static bool? _pendingIsMigratingArrival;
    private static string? _pendingName;

    internal static ScavPlayer? PendingArrivalPlayer;

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TransportLiteNetLib), "LiteNetLib.INetEventListener.OnConnectionRequest");

    private static void Prefix(ConnectionRequest request)
    {
        _pendingSteamId = null;
        _pendingClientId = null;
        _pendingIsReturning = null;
        _pendingIsMigratingArrival = null;
        _pendingName = null;
        PendingArrivalPlayer = null;

        byte[] raw = request.Data.RawData;
        int offset = request.Data.UserDataOffset;
        int size = request.Data.UserDataSize;
        if (!TailCodec.TryFind(raw, offset, size, out TailInfo tail))
            return;

        _pendingSteamId = tail.SteamId;
        _pendingClientId = tail.ClientId;
        _pendingIsReturning = tail.IsReturning;
        _pendingIsMigratingArrival = tail.IsMigratingArrival;
        _pendingName = tail.Name;
    }

    private static void Postfix()
    {
        ScavPlayer? plr = PendingArrivalPlayer;
        PendingArrivalPlayer = null;
        if (plr == null) return;
        CasuMod.MigrationModule.SendRegenerateWorld(plr); // peer 매핑 등록 후 10016 발신
    }

    internal static ulong? TakeSteamId() { var v = _pendingSteamId; _pendingSteamId = null; return v; }
    internal static ushort? TakeClientId() { var v = _pendingClientId; _pendingClientId = null; return v; }
    internal static bool? TakeIsMigrating() { var v = _pendingIsMigratingArrival; _pendingIsMigratingArrival = null; return v; }
    internal static bool TakeIsReturning() { var v = _pendingIsReturning ?? false; _pendingIsReturning = null; return v; }
    internal static string? TakeName() { var v = _pendingName; _pendingName = null; return v; }
}

internal static class ReturningTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

internal static class MigrationArrivalTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

[HarmonyPatch(typeof(BasePlayer), "ServerCreatePlayer")]
internal static class BasePlayer_ServerCreatePlayer_ApplyIdentityPatch
{
    private static void Postfix(BasePlayer __result)
    {
        if (__result == null || __result.player == null)
            return;
        ScavPlayer plr = __result.player;

        ulong? steamId = OnConnectionRequest_TailV2Patch.TakeSteamId();
        bool isMigrating = OnConnectionRequest_TailV2Patch.TakeIsMigrating() ?? false;
        bool isReturning = OnConnectionRequest_TailV2Patch.TakeIsReturning();
        if (isReturning || isMigrating)
        {
            ReturningTracker.ClientIds.Add(plr.playerId);
        }
        if (isMigrating)
        {
            MigrationArrivalTracker.ClientIds.Add(plr.playerId);
            OnConnectionRequest_TailV2Patch.PendingArrivalPlayer = plr;
        }

        if (steamId.HasValue && steamId.Value != 0)
        {
            plr.SteamId = steamId.Value;
            __result.playerSteamId.Value = steamId.Value;
        }

        string? tailName = OnConnectionRequest_TailV2Patch.TakeName();
        if (!string.IsNullOrEmpty(tailName))
        {
            plr.playerName = tailName;
            plr.ApplyNameAndColor(tailName, plr.playerColor);
        }

        SaveModule.OnPlayerArrival(CasuMod.MigrationModule.PlayerKeyOf(plr)); // 이전 방문 데이터 무효화

        if (OrchestratorClient.Instance != null)
        {
            OrchestratorClient.Instance.SendEvent(MessageType.PLAYER_DATA_REQUEST, // 저장 데이터 요청
                new { playerKey = CasuMod.MigrationModule.PlayerKeyOf(plr) });
        }
    }
}
