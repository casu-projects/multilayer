using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod.Patch;

// 서버의 액체에 관련한 계산 범위를 플레이중인 유저 주변으로 옮김
// 이전 코드에서는 유저 한명마다 많은 영역을 FixedUpdate의 시간마다 다시 계산했는데
// 이러면 다수의 유저가 한곳에 모이면 같은 액체의 이동과 계산을 인원수만큼 여러번 반복 계산하는 방식이라 서버 렉이걸려요
// 그래서 유저의 계산 범위가 겹치거나 맞닿은 범위를 먼저 합친 뒤, 서로 떨어진 영역에 대해서만 SimulationStep을 한 번씩 호출하게 변경했어요
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
internal static class FluidSimRangeFollowPlayersPatch
{
    private const int RangeBlocks = 64;

    private struct SimulationArea
    {
        internal int MinX;
        internal int MaxX;
        internal int MinY;
        internal int MaxY;
    }

    private static readonly List<SimulationArea> Areas = new();

    // 클라이언트 FluidManager.SimulationRangeIndex와 동일한 밴드 순환 (0 -> 96, +16/프레임).
    private static int simIndex;

    private static bool Prefix(FluidManager __instance)
    {

        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld) return true;

        Areas.Clear();
        foreach (NetPlayer player in NetPlayer.AllLivingPlayers)
        {
            if (player == null || player.body == null) continue;

            Vector2Int center = world.WorldToBlockPos(player.body.transform.position);
            AddAndMerge(new SimulationArea
            {
                MinX = Clamp(center.x - RangeBlocks, (int)world.width),
                MaxX = Clamp(center.x + RangeBlocks, (int)world.width),
                MinY = Clamp(center.y - RangeBlocks, (int)world.height),
                MaxY = Clamp(center.y + RangeBlocks, (int)world.height)
            });
        }

        if (Areas.Count == 0) return true;

        foreach (SimulationArea area in Areas)
        {
            // x 범위는 영역 전체, y만 클라와 동일한 16행 밴드 (범위 밖은 클램프로 생략).
            int bandMin = Mathf.Max(area.MinY, area.MinY + simIndex);
            int bandMax = Mathf.Min(area.MaxY, area.MinY + simIndex + 16);
            if (bandMin >= bandMax) continue;

            FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (
                new RangeI(area.MinX, area.MaxX),
                new RangeI(bandMin, bandMax));
            FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
            __instance.SimulationStep();
        }

        simIndex += 16;
        if (simIndex >= 112) simIndex = 0;
        return false;
    }

    private static void AddAndMerge(SimulationArea area)
    {
        // 유저들의 계산 구역을 합친 뒤 다른 영역과 새로 겹칠 수도 있으니 처음부터 다시 검사
        for (int i = 0; i < Areas.Count;)
        {
            if (!CanMergeWithoutExtraWork(Areas[i], area))
            {
                i++;
                continue;
            }

            SimulationArea existing = Areas[i];
            area.MinX = Mathf.Min(area.MinX, existing.MinX);
            area.MaxX = Mathf.Max(area.MaxX, existing.MaxX);
            area.MinY = Mathf.Min(area.MinY, existing.MinY);
            area.MaxY = Mathf.Max(area.MaxY, existing.MaxY);
            Areas.RemoveAt(i);
            i = 0;
        }

        Areas.Add(area);
    }

    private static bool CanMergeWithoutExtraWork(SimulationArea a, SimulationArea b)
    {
        if (a.MinX > b.MaxX + 1 || a.MaxX + 1 < b.MinX
            || a.MinY > b.MaxY + 1 || a.MaxY + 1 < b.MinY)
        {
            return false;
        }

        long mergedWidth = (long)Mathf.Max(a.MaxX, b.MaxX) - Mathf.Min(a.MinX, b.MinX) + 1L;
        long mergedHeight = (long)Mathf.Max(a.MaxY, b.MaxY) - Mathf.Min(a.MinY, b.MinY) + 1L;
        long mergedCells = mergedWidth * mergedHeight;
        long separateCells = AreaSize(a) + AreaSize(b);

        return mergedCells <= separateCells;
    }

    private static long AreaSize(SimulationArea area) =>
        ((long)area.MaxX - area.MinX + 1L) * ((long)area.MaxY - area.MinY + 1L);

    private static int Clamp(int value, int worldSize)
    {
        int max = Mathf.Max(1, worldSize - 2);
        return Mathf.Clamp(value, 1, max);
    }
}
