using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using UnityEngine;

namespace CasuMod;

/// <summary>헤드리스 서버 지진 파괴 수정 (2026-08-06, B안 — 플레이어별 파괴).
/// 바닐라: WorldGeneration.Update의 지진 파괴(WorldGeneration.cs:934-937)가
/// PlayerCamera.main.body(헤드리스 서버에선 주차된 호스트 바디 (482,490))를 중심으로
/// 5~30 블록 반경의 랜덤 블록 1개를 확률적으로 파괴한다 → 플레이어 주변에선 아무것도 안
/// 무너지고, 클라 로컬 RNG 파괴만 발생해 타일 해시 재동기화(WorldChunkSync)로 원복된다.
/// 수정: 서버에서 지진 파괴를 각 플레이어 위치 기준으로 실행한다. 확률(16×강도/s)·반경
/// (5~30)·RNG 방식은 바닐라와 동일하고, 플레이어 수만큼 파괴율이 늘어난다 (원작의
/// "카메라(플레이어)마다 붕괴" 의도의 충실한 번역).
/// 구현: ① 트랜스파일러가 바닐라 단일 파괴 조건(16f * earthquakeIntensity)을 0으로 무효화,
/// ② Prefix가 서버 전용으로 플레이어별 파괴를 대신 수행 — SetBlock → 기존 10007 브로드캐스트로
/// 전 클라 동기화 → 서버가 부순 블록은 유지된다.
/// 잔존 한계: 클라 로컬 RNG 파괴(플리커)는 클라 패치 불가 제약으로 남는다.</summary>
[HarmonyPatch(typeof(WorldGeneration), "Update")]
internal static class HeadlessEarthquakePatch
{
    /// <summary>바닐라 지진 파괴율 (초당 확률 계수 — WorldGeneration.cs:934와 동일).</summary>
    private const float QuakeDestructionRate = 16f;

    /// <summary>서버: 플레이어별 지진 파괴 — 바닐라 조건식과 동일한 확률, 중심 = 각 플레이어 위치.
    /// (바닐라 단일 파괴는 트랜스파일러로 무효화됨)</summary>
    private static void Prefix(WorldGeneration __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance == null || __instance.earthquakeIntensity <= 0f) return;

        foreach (NetPlayer plr in NetPlayer.AllLivingPlayers)
        {
            if (plr == null || plr.body == null) continue;
            if (UnityEngine.Random.value / Time.deltaTime < QuakeDestructionRate * __instance.earthquakeIntensity)
            {
                __instance.SetBlock(
                    __instance.WorldToBlockPos(
                        (Vector2)plr.body.transform.position
                        + UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(5f, 30f)), 0);
            }
        }
    }

    /// <summary>바닐라 단일 파괴 무효화 — `16f * earthquakeIntensity`의 16f를 0f로 교체
    /// (Random.value/dt &lt; 0이 항상 false → 바닐라 파괴 분기 미실행).
    /// Update 내 다른 16f 상수와 혼동하지 않도록 earthquakeIntensity 필드 곱셈 컨텍스트 확인.</summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].opcode == OpCodes.Ldc_R4
                && list[i].operand is float f && f == QuakeDestructionRate
                && IsEarthquakeIntensityMultiply(list, i))
            {
                list[i].operand = 0f;
            }
            yield return list[i];
        }
    }

    /// <summary>i 위치의 상수 뒤(최대 6 명령 내)에 ldfld earthquakeIntensity가 mul보다 먼저 오는지.</summary>
    private static bool IsEarthquakeIntensityMultiply(List<CodeInstruction> list, int i)
    {
        int end = Mathf.Min(list.Count, i + 6);
        for (int j = i + 1; j < end; j++)
        {
            if (list[j].opcode == OpCodes.Ldfld
                && list[j].operand != null
                && list[j].operand.ToString().Contains("earthquakeIntensity"))
            {
                return true;
            }
            if (list[j].opcode == OpCodes.Mul)
            {
                return false;
            }
        }
        return false;
    }
}
