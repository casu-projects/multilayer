using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

// late-join 스폰을 레이어 표준 스폰으로 교체 (기존 유저 위 겹침 방지) — 바닥 위 검증 포함.
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
