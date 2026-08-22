using System;
using HarmonyLib;
using KrokoshaCasualtiesMP;

namespace CasuMod.Patch;

// CASU_START_DEPTH 환경변수 -> 월드젠 시작 깊이 반영 (옛 모드 이식)
// depth-2+ 인스턴스는 biomeDepth/totalTraveled를 깊이에 맞게 설정해야 레이어 전환이
// 올바른 레이어로 생성되고, 위치 복원의 레이어 불일치 체크가 의미를 가진다
// depth-1은 기존 동작과 동일 (debugStartDepth=0, totalTraveled=0)
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

        // 클라이언트 월드젠 파라미터에 깊이 반영 (ClientMain이 firstworldgenparams로 생성)
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.totalTraveled = correctedTotalTraveled;
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.biomeDepth = (byte)__state;
    }
}
