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

[HarmonyPatch(typeof(GunScript_Fire_MultiplayerPatch), "ServerReceiver__PlayerBodyShoot")]
internal static class GunNearBarrelTolerancePatch
{
    private const float MaxSpeedAllowance = 40f; // world units/sec
    private const float BaseMargin = 4f;

    private static ushort _clientTick;

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

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo target = AccessTools.Method(typeof(KM), "DistanceCheck",
            new[] { typeof(Vector2).MakeByRefType(), typeof(Vector2).MakeByRefType(), typeof(float) });
        if (target == null)
        {
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

    private static bool TolerantDistanceCheck(in Vector2 a, in Vector2 b, float max)
    {
        int lateTicks = Mathf.Abs((short)(Net.ServerTick - _clientTick));
        float extra = lateTicks / (float)Net.ServerEntitySendRate * MaxSpeedAllowance + BaseMargin;

        bool original = KM.DistanceCheck(in a, in b, max);
        bool relaxed = original || KM.DistanceCheck(in a, in b, max + extra); // RTT만큼 여유 추가
        return relaxed;
    }
}
