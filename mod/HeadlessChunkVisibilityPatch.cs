using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CasuMod;

// 청크 활성화를 주차된 서버 카메라 대신 실제 플레이어 기준으로 갱신한다 — 유저가 지나간
// 청크는 잠시 유지해 액체/아이템/AI가 멈추는 현상을 줄인다.
[HarmonyPatch]
internal static class HeadlessChunkVisibilityPatch
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

        // 접속/월드젠 틈(준비된 플레이어 없음)에는 기존 상태를 건드리지 않는다.
        bool hasReadyPlayer = false;
        foreach (NetPlayer player in NetPlayer.AllLivingPlayers)
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

            bool interested = SharedMain.CheckIfChunkOnThisPositionIsVisibleByAnyPlayer(
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

        // 월드 재생성으로 파괴된 Renderer 키 정리.
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
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        HeadlessChunkVisibilityPatch.RefreshPlayerInterest();
    }
}

// 바닐라 UpdateChunkVisibility(주차 카메라 기준 재비활성화)를 서버에서 건너뛴다.
[HarmonyPatch]
internal static class HeadlessChunkVisibility_UpdateChunkVisibilityHook
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(WorldGeneration), "UpdateChunkVisibility");
    }

    private static bool Prefix()
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return true;
        HeadlessChunkVisibilityPatch.RefreshPlayerInterest();
        return false;
    }
}
