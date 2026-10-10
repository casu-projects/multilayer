using CasuMod;
using System;
using System.Reflection;
using HarmonyLib;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod.Patch;

// tail v2/v3 (/): connect data 끝 [Magic C5A5][Ver 1|2][SteamID64 8B][clientId 2B]
// [isReturning 1B][isMigratingArrival 1B] - Ver 2는 뒤에 [nameLen 1B][UTF-8 이름] 추가
// (CJK 이름 보정용 - 게이트웨이가 Steam 유저명을 실어 보낸다). Magic/Ver 불일치 시 접속 거부
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

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TransportLiteNetLib), "LiteNetLib.INetEventListener.OnConnectionRequest");

    private static void Prefix(ConnectionRequest request, ref bool __runOriginal)
    {
        _pendingSteamId = null;
        _pendingClientId = null;
        _pendingIsReturning = null;
        _pendingIsMigratingArrival = null;
        _pendingName = null;

        byte[] raw = request.Data.RawData;
        int offset = request.Data.UserDataOffset;
        int size = request.Data.UserDataSize;
        if (size < FixedTailSize)
            return;

        // tail은 connect data 끝에 붙는다 - Ver 2는 이름 길이만큼 길어지므로
        // 끝에서부터 Magic을 역탐색한다 (이름 최대 128바이트 상한).
        // 버전 바이트(1|2)를 함께 검증해 이름 내 우연한 C5A5 오검출을 건너뛴다.
        int tailStart = -1;
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
        if (version != Version1 && version != Version2)
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

        // NetDataReader 3번째 인자는 "길이"가 아니라 절대 끝 위치(_dataSize)다
        // (SetSource: _dataSize = maxSize, EnsureAvailable 은 _dataSize - _position 으로 판정).
        // 고정부(12B)가 user data 안에 온전히 존재하는지 먼저 보장한다.
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

    internal static ulong? TakeSteamId() { var v = _pendingSteamId; _pendingSteamId = null; return v; }
    internal static ushort? TakeClientId() { var v = _pendingClientId; _pendingClientId = null; return v; }
    internal static bool? TakeIsMigrating() { var v = _pendingIsMigratingArrival; _pendingIsMigratingArrival = null; return v; }
    internal static bool TakeIsReturning() { var v = _pendingIsReturning ?? false; _pendingIsReturning = null; return v; }
    internal static string? TakeName() { var v = _pendingName; _pendingName = null; return v; }
}

// 복귀(재접속/세션 복원) 플레이어 clientId 집합 - 마이그레이션 도착 감시에 사용
internal static class ReturningTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

// 마이그레이션 도착 플레이어 clientId 집합 - 접속 공지 억제 전용
// (isMigratingArrival=true - 게이트웨이 SwapBackend만 true, 일반 재접속은 미등록)
internal static class MigrationArrivalTracker
{
    internal static readonly System.Collections.Generic.HashSet<knetid> ClientIds = new();
}

// 신원 적용: LES 는 BasePlayer.ServerCreatePlayer 로 원격 플레이어 엔티티를 생성한다.
// tail(SteamID/이름/복귀·도착 플래그)을 여기서 반영한다. (구 CreateNetPlayerWithPeer Postfix 대체)
// GetNextPlayerId 강제 clientId 훅은 신버전 LES 에 없어 제거 - 신원 키는 SteamID/name 기반.
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
        }

        if (steamId.HasValue && steamId.Value != 0)
        {
            plr.SteamId = steamId.Value;
            __result.playerSteamId.Value = steamId.Value;
        }

        // tail(Ver 2)로 전달된 Steam 보정 이름 - 인트로의 1바이트/문자 인코딩으로 깨진
        // CJK 유저명을 덮어쓴다 (채팅 발신자/로스터/신원 동기화에 반영).
        string? tailName = OnConnectionRequest_TailV2Patch.TakeName();
        if (!string.IsNullOrEmpty(tailName))
        {
            plr.playerName = tailName;
            plr.ApplyNameAndColor(tailName, plr.playerColor);
        }

        // 도착 epoch: 직전 방문의 잔존 PendingData/PendingPositions 소비 차단
        // 바디 스폰(Body.Start)보다 반드시 먼저 실행된다 (ServerCreatePlayer 동기 처리)
        SaveModule.OnPlayerArrival(plr.GetPersistentId());

        // 접속 시 플레이어 데이터 요청 - 직접연결(SteamId=0) 포함 전 플레이어
        // playerKey는 GetPersistentId (SteamId 있으면 STEAM_, 없으면 NAME_hex)
        if (OrchestratorClient.Instance != null)
        {
            OrchestratorClient.Instance.SendEvent("PLAYER_DATA_REQUEST",
                new { playerKey = plr.GetPersistentId() });
        }
    }
}
