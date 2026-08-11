using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;

namespace CasuMod;

// 리스폰 시 opiate 상태 초기화 — 바닐라의 지연 파괴로 클라 로컬 Painkillers 컴포넌트가
// 스테일 상태로 남는 것을, 전 필드 0인 10128 패킷 브로드캐스트로 완전 리셋한다.
[HarmonyPatch(typeof(NetPlayer), nameof(NetPlayer.Server_RespawnCharacter))]
internal static class NetPlayer_Server_RespawnCharacter_OpiateResetPatch
{
    private static void Postfix(NetPlayer __instance)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (__instance.playerbody == null) return;

        var writer = Net.CreateWriter(10128);
        writer.Put((ushort)__instance.playerbody.netId);
        MyLiteNetLibExtensions.Put(writer, default(CharacterHealthPainkillerStateSyncPacket));
        writer.Put(false);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, (IEnumerable<knetid>)ServerMain.AllClientIdsExceptHost);
    }
}

// 리스폰/리바이브 시 usedNeuralBooster 초기화 — 잔존 플래그로 재투약 시 2회차 치명 부작용이
// 발동하는 것을 방지한다. ResetHealth 후 10127 싱크가 false를 실어 클라 로컬도 보정된다.
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
        if (!KrokoshaScavMultiplayer.is_dedicated_server) return;
        if (body == null) return;
        body.usedNeuralBooster = false;
    }
}
