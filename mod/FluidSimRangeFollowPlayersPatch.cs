using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>유체 시뮬레이션 범위를 모든 플레이어 기준으로 강제 (2026-08-07, v3).
/// 문제: 바닐라 FluidManager.SimulationStep의 시뮬레이션 범위(SimulationRangeIndex —
/// FluidManager.cs:101-105)가 PlayerCamera.main.transform.position(주차된 서버 카메라) 기준
/// ±64블록이라, 플레이어가 그 밖에 있으면 서버 유체 배열이 갱신되지 않는다 → 서버 권위
/// 동기화(WorldChunkSync.FluidTilemapSyncUpdate)가 클라 로컬 시뮬레이션을 원복해
/// 유체가 퍼지지 않거나 되돌아간다 (Fluid 동기화 이상).
/// v2: 플레이어별 라운드로빈(1/N) — 프로덕션 다중 유저에서 각자 1/N 속도로만 시뮬레이션되어
/// 서버 유체가 클라를 못 따라잡음 → "유체 초기화" 관측 (2026-08-07, 프로덕션 조사).
/// v3: 라운드로빈 제거 — FixedUpdate마다 **모든 플레이어의 영역을 전부 시뮬레이션**한다
/// (MP forcenext 훅으로 범위 강제 + SimulationStep을 플레이어 수만큼 호출).
/// 모든 유저가 풀레이트 — 서버가 클라를 따라잡아 원복이 사라진다.
/// 반경 64 (성능: N×16,384 타일/프레임 — 타일당 분기 체크 수준).
/// 범위 경계는 바닐라와 동일하게 클램프 (1 ~ worldSize-2). 헤드리스/그래픽 무관.</summary>
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
internal static class FluidSimRangeFollowPlayersPatch
{
    private const int RangeBlocks = 64;

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
            FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (
                ClampRange(c.x - RangeBlocks, c.x + RangeBlocks, (int)world.width),
                ClampRange(c.y - RangeBlocks, c.y + RangeBlocks, (int)world.height));
            FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
            __instance.SimulationStep();
        }

        // 바닐라 단일 SimulationStep은 이미 플레이어별로 대체 실행 — 스킵.
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
