using System;
using System.Collections;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod.Patch;

// 마이그레이션 도착 클라의 스폰 리마인더(BodyTeleportPacket, 66) 유실 대응.
// 서버는 WorldPlacePlayer(205)에서 리마인더를 한 번만 보내고, 클라는 NetBody.TryGetNetBodyFromId가
// 실패하면 조용히 return한다. 도착지 첫 baseline 전이거나 월드젠 재생성으로 NB가 사라진 순간이면
// 스폰이 영구 유실되므로(클라는 이 리마인더를 받아야 FinishedWorldgen으로 넘어간다),
// 마이그레이션 도착 플레이어에 한해 finished_worldgen 전까지 멱등 재전송한다.
[HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_WorldPlacePlayer")]
internal static class MigrationSpawnRetryPatch
{
    // 첫 리마인더(원본 코루틴의 스폰 위치 배치) 이후부터 재전송을 시작한다
    private const float StartDelaySeconds = 0.5f;
    private const float IntervalSeconds = 0.3f;
    private const int MaxAttempts = 24;

    private static void Postfix(knetid clientId)
    {
        if (!MigrationArrivalTracker.ClientIds.Contains(clientId)) return;
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null) return;

        ((MonoBehaviour)plr).StartCoroutine(Retry(plr));
    }

    private static IEnumerator Retry(ScavPlayer plr)
    {
        yield return new WaitForSecondsRealtime(StartDelaySeconds);

        for (int i = 0; i < MaxAttempts; i++)
        {
            if (plr == null) yield break;
            if (plr.ServerPlayerState != null && plr.ServerPlayerState.finished_worldgen) yield break;

            if (Body_PlaceBody_MultiplayerPatch.has_spawn_location && plr.body != null && Util.IsWorldGenerated())
            {
                try
                {
                    plr.Server_RemindPlayersCurrentState();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Migration] 스폰 리마인더 재전송 실패: {ex.Message}");
                    yield break;
                }
            }

            yield return new WaitForSecondsRealtime(IntervalSeconds);
        }
    }
}
