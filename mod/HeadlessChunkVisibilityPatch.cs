using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CasuMod;

// 헤드리스 서버의 청크 활성화를 고정된 호스트의 카메라가 아니라 실제 플레이 중인 유저 기준으로 바꿈
// 유저가 해당 청크를 지나가도 잠시 동안 유지해서 액체, 떨어지는 아이템이나 상자, AI가 멈추는 현상을 줄인다
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

        // 실제 유저 접속하지 않아서 하나도 준비되지 않은 접속/월드젠 틈에는 기존 상태를 건드리지 않게 설정
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

            // 멀티 모드가 이미 관리하는 플레이어의 가시 그리드를 그대로 가져와서 사용
            // 이러면 레이어 전체를 갱신 할 필요도 없고, 여러 플레이어가 멀리 떨어져 있어도 각자의 주변만 활성화 된다
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

        // 월드가 재생성되면 파괴된 Renderer 키가 여전히 남으니. 조금이라도 꾸준히 치워준다
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

// 게임 원본 기준으로는 월드 밖에 주차된 서버 카메라를 기준으로 청크를 다시 꺼버리지만
// 데디케이트 서버에서만 그 방식을 건너뛰고 플레이중인 유저 기준으로 갱신을 사용하게 만듬
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
