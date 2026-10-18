using HarmonyLib;
using UnityEngine;

namespace CasuMod;

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock))]
internal static class BlockDelta_SetBlockPatch
{
    private static void Postfix(Vector2Int pos, ushort block)
    {
        MapModule.Instance?.OnBlockChanged(pos, block);
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlockNoUpdate))]
internal static class BlockDelta_SetBlockNoUpdatePatch
{
    private static void Postfix(Vector2Int pos, ushort block)
    {
        MapModule.Instance?.OnBlockChanged(pos, block);
    }
}
