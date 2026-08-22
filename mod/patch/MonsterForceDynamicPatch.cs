using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod.Patch;

// 유저 근처 몬스터들의 물리 모드를 강제로 Dynamic으로 변경
[HarmonyPatch]
internal static class MonsterForceDynamicPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(BuildingEntity_Update_MultiplayerPatch), "NewBuildingOptimizeThing");
    }

    private static bool Prefix(Rigidbody2D rb)
    {
        if (rb == null) return true;
        if (WorldGeneration.world == null || !WorldGeneration.world.worldExists) return true;
        if (WorldGeneration.world.generatingWorld) return true;

        GameObject go = rb.gameObject;
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
