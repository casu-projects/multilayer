using CasuMod;
using System;
using System.Reflection;
using HarmonyLib;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod.Patch;

[HarmonyPatch]
internal static class OnConnectionRequest_TailV2Patch
{
    private const byte MagicHigh = 0xC5;
    private const byte MagicLow = 0xA5;
    private const byte Version1 = 1;
    private const byte Version2 = 2;
    private const int FixedTailSize = 15;   // 이름 제외 고정부
    private const int MaxNameBytes = 128;   // 역탐색 범위 상한

    private static ulong? _pendingSteamId;
    private static ushort? _pendingClientId;
    private static bool? _pendingIsReturning;
    private static bool? _pendingIsMigratingArrival;
    private static string? _pendingName;

    internal static ScavPlayer? PendingArrivalPlayer;

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TransportLiteNetLib), "LiteNetLib.INetEventListener.OnConnectionRequest");

    private static void Prefix(ConnectionRequest request, ref bool __runOriginal)
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
        if (size < FixedTailSize)
            return;

        int tailStart = -1; // 끝에서 Magic 역탐색 (이름 바이트 상한)
        int searchEnd = Math.Max(offset, offset + size - FixedTailSize - MaxNameBytes);
        for (int i = offset + size - 2; i >= searchEnd; i--)
        {
            if (raw[i] == MagicHigh && raw[i + 1] == MagicLow
                && i + 3 < raw.Length
                && (raw[i + 2] == Version1 || raw[i + 2] == Version2))
            {
                tailStart = i;
                break;
            }
        }
        if (tailStart < 0)
            return;

        byte version = raw[tailStart + 2];
        if (version != Version1 && version != Version2) // 버전 스큐 -> 접속 거부
        {
            Plugin.Log.LogError($"[Identity] tail v2 불일치 (Magic/Ver) — 접속 거부. "
                + "게이트웨이와 모드 버전을 맞춰야 합니다.");
            var reject = new NetDataWriter();
            reject.Put("Version mismatch: gateway/mod protocol skew.");
            request.Reject(reject);
            __runOriginal = false;
            return;
        }

        if (tailStart + FixedTailSize > offset + size)
            return;

        var r = new NetDataReader(raw, tailStart + 3, tailStart + FixedTailSize);
        _pendingSteamId = r.GetULong();
        _pendingClientId = r.GetUShort();
        _pendingIsReturning = r.GetBool();
        _pendingIsMigratingArrival = r.GetBool();

        if (version == Version2 && tailStart + FixedTailSize < raw.Length)
        {
            byte nameLen = raw[tailStart + FixedTailSize];
            if (nameLen > 0 && tailStart + FixedTailSize + 1 + nameLen <= raw.Length)
            {
                _pendingName = System.Text.Encoding.UTF8.GetString(raw, tailStart + FixedTailSize + 1, nameLen);
            }
        }
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
            OrchestratorClient.Instance.SendEvent("PLAYER_DATA_REQUEST", // 저장 데이터 요청
                new { playerKey = CasuMod.MigrationModule.PlayerKeyOf(plr) });
        }
    }
}
