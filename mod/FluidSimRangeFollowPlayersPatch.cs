using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 유체 시뮬레이션 범위를 모든 플레이어 기준으로 강제 — 바닐라는 주차된 서버 카메라 기준이라
// 플레이어 근처 유체가 서버 배열에서 갱신되지 않아 클라 로컬 시뮬레이션을 원복한다.
// v4: 바닐라와 동일한 16줄 밴드(128×16)를 플레이어 중심으로 순환 적용 — BandsPerFrame=2로
// 셀당 12.5Hz(클라 6.25Hz의 2배)를 유지하면서 전체 범위 시뮬레이션 대비 4분의 1 비용.
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
internal static class FluidSimRangeFollowPlayersPatch
{
    private const int RangeBlocks = 64;
    private const int BandHeight = 16;
    private const int BandsPerFrame = 2;

    // 밴드 오프셋 (0,16,...,112 순환).
    private static int _bandIndex;

    private static readonly List<NetPlayer> _players = new();

    private static bool Prefix(FluidManager __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld) return true;

        _players.Clear();
        foreach (NetPlayer p in NetPlayer.AllLivingPlayers)
        {
            if (p != null && p.body != null) _players.Add(p);
        }
        if (_players.Count == 0) return true;

        foreach (NetPlayer p in _players)
        {
            Vector2Int c = world.WorldToBlockPos(p.body.transform.position);
            int band = _bandIndex;
            for (int b = 0; b < BandsPerFrame; b++)
            {
                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (
                    ClampRange(c.x - RangeBlocks, c.x + RangeBlocks, (int)world.width),
                    ClampRange(c.y - RangeBlocks + band, c.y - RangeBlocks + band + BandHeight, (int)world.height));
                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
                __instance.SimulationStep();
                band = (band + BandHeight) % (RangeBlocks * 2);
            }
        }
        _bandIndex = (_bandIndex + BandHeight * BandsPerFrame) % (RangeBlocks * 2);

        return false;
    }

    // 바닐라 SimulationRangeIndex와 동일한 경계 클램프 (1 ~ size-2).
    private static RangeI ClampRange(int min, int max, int worldSize)
    {
        if (min < 1) min = 1;
        if (max < 1) max = 1;
        if (max > worldSize - 2) max = worldSize - 2;
        if (min > worldSize - 2) min = worldSize - 2;
        return new RangeI(min, max);
    }
}
