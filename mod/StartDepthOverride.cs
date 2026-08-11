using System;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod;

// CASU_START_DEPTH 환경변수를 월드젠 시작 깊이(biomeDepth/totalTraveled)에 반영 —
// depth-2+ 인스턴스가 올바른 레이어로 생성되게 한다.
[HarmonyPatch(typeof(WorldGeneration), "Start")]
internal static class WorldGeneration_Start_DepthOverridePatch
{
    private const string StartDepthEnvVar = "CASU_START_DEPTH";

    private static void Prefix(WorldGeneration __instance, out int __state)
    {
        __state = -1;

        int depth = 0;
        string raw = Environment.GetEnvironmentVariable(StartDepthEnvVar);
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int parsedDepth) && parsedDepth >= 1)
        {
            depth = parsedDepth;
        }

        __instance.debugStartDepth = depth <= 0 ? 0 : depth - 1;
        __state = __instance.debugStartDepth;
        __instance.totalTraveled = __state <= 0 ? 0 : (int)(__instance.height * 0.3f * __state);
    }

    private static void Postfix(WorldGeneration __instance, int __state)
    {
        if (__state < 0) return;

        __instance.biomeDepth = __state;
        int correctedTotalTraveled = __state <= 0
            ? 0
            : (int)(__instance.height * 0.3f * __state);
        __instance.totalTraveled = correctedTotalTraveled;

        // 클라이언트 월드젠 파라미터에도 깊이 반영.
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.totalTraveled = correctedTotalTraveled;
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.biomeDepth = (byte)__state;
    }
}
