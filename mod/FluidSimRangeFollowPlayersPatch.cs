using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>유체 시뮬레이션 범위를 플레이어별 라운드로빈으로 강제 (2026-08-07).
/// 문제: 바닐라 FluidManager.SimulationStep의 시뮬레이션 범위(SimulationRangeIndex —
/// FluidManager.cs:101-105)가 PlayerCamera.main.transform.position(주차된 서버 카메라) 기준
/// ±64블록이라, 플레이어가 그 밖에 있으면 서버 유체 배열이 갱신되지 않는다 → 서버 권위
/// 동기화(WorldChunkSync.FluidTilemapSyncUpdate)가 클라 로컬 시뮬레이션을 원복해
/// 유체가 퍼지지 않거나 되돌아간다 (Fluid 동기화 이상).
/// 해결: MP가 제공한 FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext 훅으로
/// 시뮬레이션 범위를 플레이어 위치 기준으로 강제 — 카메라 위치와 무관하게 동작.
/// 플레이어가 여럿이면 FixedUpdate마다 라운드로빈 (각자 1/N 속도 — 유체는 천천히 퍼지므로
/// 실용적). 범위 경계는 바닐라와 동일하게 클램프 (1 ~ worldSize-2).
/// 헤드리스/그래픽 무관.</summary>
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
internal static class FluidSimRangeFollowPlayersPatch
{
    /// <summary>시뮬레이션 반경(블록) — 클라 카메라 오프셋(±64±α)과 동기화 창(아래 -128)을
    /// 포함하도록 128로 확대 (64일 때 동기화 창 가장자리에서 증발 관측 — 2026-08-07).</summary>
    private const int RangeBlocks = 128;

    private static readonly List<NetPlayer> _players = new();

    private static int _nextPlayerIndex;

    private static void Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld) return;

        _players.Clear();
        foreach (NetPlayer p in NetPlayer.AllLivingPlayers)
        {
            if (p != null && p.body != null) _players.Add(p);
        }
        if (_players.Count == 0) return;

        NetPlayer target = _players[_nextPlayerIndex % _players.Count];
        _nextPlayerIndex++;

        Vector2Int c = world.WorldToBlockPos(target.body.transform.position);
        FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (
            ClampRange(c.x - RangeBlocks, c.x + RangeBlocks, (int)world.width),
            ClampRange(c.y - RangeBlocks, c.y + RangeBlocks, (int)world.height));
        FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
    }

    /// <summary>바닐라 SimulationRangeIndex와 동일한 경계 클램프 (1 ~ size-2).</summary>
    private static RangeI ClampRange(int min, int max, int worldSize)
    {
        if (min < 1) min = 1;
        if (max < 1) max = 1;
        if (max > worldSize - 2) max = worldSize - 2;
        if (min > worldSize - 2) min = worldSize - 2;
        return new RangeI(min, max);
    }
}
