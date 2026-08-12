using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 헤드리스 서버에서 플레이 중인 유저의 근처 적들만 Dynamic 물리로 깨움
// 하지만 적Ai를 전부 깨우면 CPU와 blockDamages 목록이 터지니, 그 부분은 방지
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
