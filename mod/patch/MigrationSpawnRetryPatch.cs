using System;
using System.Collections;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod.Patch;

// 마이그레이션 도착 클라이언트의 스폰 리마인더(BodyTeleportPacket, 66) 유실 대응.
//
// 서버는 WorldPlacePlayer(205) 수신 시 리마인더를 "한 번만" 보낸다
// (WorldgenPatches.ServerReceiver_WorldPlacePlayer -> ServerMain.GiveSpawnLocationToPlayer).
// 클라이언트 수신부는 NetBody.TryGetNetBodyFromId(id) 가 실패하면 조용히 return 하므로
// (ClientMain.ClientReceiver__SingleCharacterPositionsSync), 목적지 첫 baseline 으로 NB 가
// 아직 생성되지 않았거나 월드젠 재생성으로 사라진 순간이면 스폰이 영구 유실된다
// (클라이언트는 이 리마인더를 받아야 FinishedWorldgen 으로 넘어간다).
//
// 따라서 마이그레이션 도착 플레이어에 한해, 월드젠 완료(finished_worldgen) 전까지
// 현재 상태 리마인더를 짧은 간격으로 재전송한다. 재전송은 멱등이며, 클라이언트가 NB 를
// 확보하는 순간 적용된다.
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
