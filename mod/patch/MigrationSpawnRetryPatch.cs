using System;
using System.Collections;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(WorldgenPatches), "ServerReceiver_WorldPlacePlayer")]
internal static class MigrationSpawnRetryPatch
{
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
                    plr.Server_RemindPlayersCurrentState(); // 스폰 리마인더 재전송
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
