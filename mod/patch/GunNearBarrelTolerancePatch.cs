using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using UnityEngine;

namespace CasuMod.Patch;

// 총기 near-barrel 검증의 RTT 오탐 완화 (base가 데미지/릴레이를 계속 소유).
// ServerReceiver__PlayerBodyShoot가 클라 발사 원점과 서버 현재 총구를 DistanceCheck로 비교해
// 벗어나면 발사를 거부하는데, RTT 동안 이동하면 오탐이 난다. 원본 메서드를 재구현하지 않고
// DistanceCheck 호출만 동일 시그니처 헬퍼로 치환해 클라-서버 틱 지연만큼 여유를 더한다.
// IL 패턴이 바뀌면 치환 0건으로 원본 유지, 파싱 실패 시 여유 0으로 후퇴한다.
[HarmonyPatch(typeof(GunScript_Fire_MultiplayerPatch), "ServerReceiver__PlayerBodyShoot")]
internal static class GunNearBarrelTolerancePatch
{
    // RTT 흡수 상한 (보수적) - 클라-서버 틱 지연 x 최대 이동속도
    private const float MaxSpeedAllowance = 40f; // world units/sec
    private const float BaseMargin = 4f;

    // 이번 패킷에서 클라가 본 ServerTick (Prefix 가 캡처)
    private static ushort _clientTick;

    // reader 를 소비하지 않고 선두 ushort(클라 틱)만 읽어 캡처 후 위치 복원
    private static void Prefix(NetDataReader reader)
    {
        try
        {
            int pos = reader.Position;
            _clientTick = reader.GetUShort();
            reader.SetPosition(pos);
        }
        catch
        {
            _clientTick = Net.ServerTick;
        }
    }

    // near-barrel 검사의 DistanceCheck 호출만 동일 시그니처 헬퍼로 교체
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // KM.DistanceCheck 는 in Vector2 파라미터(=Vector2&) 오버로드이므로 by-ref 타입으로 조회한다
        MethodInfo target = AccessTools.Method(typeof(KM), "DistanceCheck",
            new[] { typeof(Vector2).MakeByRefType(), typeof(Vector2).MakeByRefType(), typeof(float) });
        if (target == null)
        {
            // 시그니처가 바뀌어도 견디도록 이름/파라미터로 폴백 탐색
            target = AccessTools.GetDeclaredMethods(typeof(KM)).FirstOrDefault(m =>
            {
                if (m.Name != "DistanceCheck") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 3
                    && p[0].ParameterType == typeof(Vector2).MakeByRefType()
                    && p[1].ParameterType == typeof(Vector2).MakeByRefType()
                    && p[2].ParameterType == typeof(float);
            });
        }
        MethodInfo replacement = AccessTools.Method(typeof(GunNearBarrelTolerancePatch), nameof(TolerantDistanceCheck));
        if (target == null || replacement == null)
        {
            Plugin.Log.LogWarning("[GunFix] DistanceCheck 치환 대상 해석 실패 — 원본 유지.");
            foreach (var ins in instructions) yield return ins;
            yield break;
        }

        bool patched = false;
        foreach (CodeInstruction ins in instructions)
        {
            if (ins.opcode == OpCodes.Call && Equals(ins.operand, target))
            {
                ins.operand = replacement;
                patched = true;
            }
            yield return ins;
        }
        if (patched)
        {
            Plugin.Log.LogInfo("[GunFix] near-barrel DistanceCheck 치환 적용.");
        }
        else
        {
            Plugin.Log.LogWarning("[GunFix] near-barrel 검사 패턴 미발견 — 원본 유지(무해).");
        }
    }

    // 원본 검사에 클라-서버 틱 지연만큼의 이동 여유를 더한다 (국소 완화)
    private static bool TolerantDistanceCheck(in Vector2 a, in Vector2 b, float max)
    {
        int lateTicks = Mathf.Abs((short)(Net.ServerTick - _clientTick));
        float extra = lateTicks / (float)Net.ServerEntitySendRate * MaxSpeedAllowance + BaseMargin;

        bool original = KM.DistanceCheck(in a, in b, max);
        bool relaxed = original || KM.DistanceCheck(in a, in b, max + extra);
        return relaxed;
    }
}
