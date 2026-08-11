using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>유체 시뮬레이션 범위를 모든 플레이어 기준으로 강제 (v4 — 밴드 방식, 2026-08-11).
/// 문제: 바닐라 FluidManager.SimulationStep의 시뮬레이션 범위(SimulationRangeIndex)가
/// PlayerCamera.main(주차된 서버 카메라) 기준이라, 플레이어가 그 밖에 있으면 서버 유체
/// 배열이 갱신되지 않아 서버 권위 동기화가 클라 로컬 시뮬레이션을 원복한다.
/// v3: 플레이어당 ±64 전체 128×128을 매 FixedUpdate 시뮬레이션 (16,384셀/플레이어/50Hz) —
/// 클라이언트(밴드 방식, 셀당 6.25Hz) 대비 8배 오버프로비저닝이라 CPU 부담이 크다.
/// v4: 바닐라와 동일한 16줄 밴드를 플레이어 중심으로 적용 — BandsPerFrame=2로 셀당 12.5Hz
/// (= 클라 2배)를 유지하면서 비용은 4분의 1 (7명: 114K → 29K 셀/프레임). 서버 셀당 처리율이
/// 클라 이상이므로 서버 유체 배열이 뒤처질 수 없다 (v2 라운드로빈 실패 원인 차단).
/// 범위 경계는 바닐라와 동일하게 클램프 (1 ~ worldSize-2).</summary>
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
internal static class FluidSimRangeFollowPlayersPatch
{
    private const int RangeBlocks = 64;
    private const int BandHeight = 16;
    private const int BandsPerFrame = 2;

    /// <summary>밴드 오프셋 (0,16,...,112 순환) — 전 플레이어 공유.</summary>
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

        // 바닐라 단일 SimulationStep은 플레이어별로 대체 실행 — 스킵.
        return false;
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
