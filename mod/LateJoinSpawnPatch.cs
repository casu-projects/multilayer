using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// 바닐라 late-join 스폰(다른 생존 플레이어 위에 겹쳐 스폰 - ServerMain.LateSpawnLocation의
// GetPlayerBodyHostOrAnyoneAlive 분기)을 레이어 표준 스폰(spawnlocation)으로 교체 (이전 모드 이식)
// 모든 위치는 바닥 위 검증(OverlapBox 미충돌 + 3f 아래 바닥 Raycast) - 공중/구덩이 스폰 방지
// 재접속자는 did_give_spawn_location_from_a_save 플래그로 이 경로를 타지 않는다
[HarmonyPatch(typeof(ServerMain), nameof(ServerMain.LateSpawnLocation))]
internal static class LateJoinSpawnPatch
{
    private const int MaxRandomAttempts = 40;
    private const float MaxRadius = 20f;
    private const float FloorCheckDistance = 3f;

    private static bool Prefix(NetBody b)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server)
        {
            return true;
        }

        WorldGeneration world = WorldGeneration.world;
        if (world == null)
        {
            return true;
        }

        Vector2 anchor = Body_PlaceBody_MultiplayerPatch.spawnlocation;
        Vector2 colSize = Body_Awake_MultiplayerPatch.origColSize;

        Vector2? safe = FindSafePosition(anchor, colSize);
        b.SetBodyPosition(safe ?? anchor);
        return false;
    }

    private static Vector2? FindSafePosition(Vector2 anchor, Vector2 colSize)
    {
        if (IsSpawnSafe(anchor, colSize))
        {
            return anchor;
        }

        for (int i = 0; i < MaxRandomAttempts; i++)
        {
            Vector2 candidate = anchor + Random.insideUnitCircle * MaxRadius;
            if (IsSpawnSafe(candidate, colSize))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsSpawnSafe(Vector2 pos, Vector2 colSize)
    {
        if (Physics2D.OverlapBox(pos, colSize, 0f, LayerMask.GetMask("Ground")))
        {
            return false;
        }

        RaycastHit2D hit = Physics2D.Raycast(pos, Vector2.down, FloorCheckDistance, LayerMask.GetMask("Ground"));
        return hit.collider != null;
    }
}
