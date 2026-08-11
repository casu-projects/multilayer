using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 플레이어 가시 범위의 적만 Dynamic 물리로 전환 — 전부 깨우면 CPU/blockDamages가 폭주한다.
[HarmonyPatch]
internal static class HeadlessMonsterForceDynamicPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(BuildingEntity_Update_MultiplayerPatch), "NewBuildingOptimizeThing");
    }

    private static bool Prefix(Rigidbody2D rb)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server || rb == null) return true;
        if (WorldGeneration.world == null || !WorldGeneration.world.worldExists) return true;
        if (WorldGeneration.world.generatingWorld) return true;

        GameObject go = rb.gameObject;
        // 스파이더/그랩버플랜트 또는 animal 플래그 빌딩만 대상.
        if (go.GetComponent<SpiderHandler>() == null && go.GetComponent<GrabberPlant>() == null)
        {
            BuildingEntity building = go.GetComponent<BuildingEntity>();
            if (building == null || !building.animal) return true;
        }

        if (SharedMain.CheckIfChunkOnThisPositionIsVisibleByAnyPlayer(rb.position))
        {
            rb.bodyType = RigidbodyType2D.Dynamic;
            return false;
        }

        return true;
    }
}
