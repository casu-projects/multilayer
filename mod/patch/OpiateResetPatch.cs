using System;
using System.Collections.Generic;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.Server_RespawnCharacter))]
internal static class NetPlayer_Server_RespawnCharacter_OpiateResetPatch
{
    private static void Postfix(ScavPlayer __instance)
    {
        if (__instance.body == null) return;
        NetBody nb = __instance.netBody;
        if (nb == null) return;

        var writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_HealthPainkillerSync);
        writer.Put((ushort)nb.netId);
        MyLiteNetLibExtensions.Put(writer, default(CharacterHealthPainkillerStateSyncPacket));
        writer.Put(false);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, (IEnumerable<knetid>)ServerMain.AllClientIdsExceptHost); // 전 필드 0 리셋 브로드캐스트
    }
}

[HarmonyPatch]
internal static class Body_ResetHealth_NeuralBoosterResetPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Util_BodyExtensions), "ResetHealth",
            new[] { typeof(Body), typeof(bool) });
    }

    private static void Postfix(Body body)
    {
        if (body == null) return;
        body.usedNeuralBooster = false; // 리스폰/리바이브 시 플래그 초기화
    }
}
