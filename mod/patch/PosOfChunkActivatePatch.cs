using System.Collections.Generic;
using HarmonyLib;
using Together;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CasuMod.Patch;

[HarmonyPatch]
internal static class PosOfChunkActivatePatch
{
    private const float RefreshIntervalSeconds = 0.2f;
    private const float ActiveLingerSeconds = 10f;

    private static readonly Dictionary<TilemapRenderer, float> LastInterestedAt = new();
    private static float _nextRefreshAt;

    internal static void RefreshPlayerInterest()
    {
        if (WorldGeneration.world == null || WorldGeneration.world.generatingWorld) return;
        if (Time.unscaledTime < _nextRefreshAt) return;
        _nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;

        TilemapRenderer[,] renderChunks = WorldGeneration.world.renderChunks;
        if (renderChunks == null) return;

        bool hasReadyPlayer = false;
        foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers)
        {
            if (player != null && player.body != null)
            {
                hasReadyPlayer = true;
                break;
            }
        }
        if (!hasReadyPlayer) return;

        float now = Time.unscaledTime;
        var aliveRenderers = new HashSet<TilemapRenderer>();

        foreach (TilemapRenderer renderer in renderChunks)
        {
            if (renderer == null) continue;
            aliveRenderers.Add(renderer);

            bool interested = SharedMain.PositionIsInSimulatedChunk(
                renderer.transform.position);

            if (interested)
            {
                LastInterestedAt[renderer] = now;
                if (!renderer.enabled) renderer.enabled = true;
                continue;
            }

            bool lingering = LastInterestedAt.TryGetValue(renderer, out float lastInterested)
                && now - lastInterested < ActiveLingerSeconds;
            renderer.enabled = lingering;
        }

        var stale = new List<TilemapRenderer>();
        foreach (TilemapRenderer renderer in LastInterestedAt.Keys)
        {
            if (renderer == null || !aliveRenderers.Contains(renderer)) stale.Add(renderer);
        }
        foreach (TilemapRenderer renderer in stale) LastInterestedAt.Remove(renderer);
    }
}

[HarmonyPatch]
internal static class HeadlessChunkVisibility_SharedMainUpdateHook
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(SharedMain), "Update");
    }

    private static void Prefix()
    {
        PosOfChunkActivatePatch.RefreshPlayerInterest();
    }
}

[HarmonyPatch]
internal static class HeadlessChunkVisibility_UpdateChunkVisibilityHook
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(WorldGeneration), "UpdateChunkVisibility");
    }

    private static bool Prefix()
    {
        PosOfChunkActivatePatch.RefreshPlayerInterest();
        return false;
    }
}
