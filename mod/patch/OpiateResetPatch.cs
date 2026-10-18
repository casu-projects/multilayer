using System;
using System.Collections.Generic;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;

namespace CasuMod.Patch;

// 리스폰 시 opiate 상태 초기화 - Server_RespawnCharacter.
// 바닐라 리스폰이 서버측 Painkillers 컴포넌트를 Object.Destroy로 제거하지만, Unity 지연 파괴 때문에
// 같은 프레임 10127 헬스 싱크의 painkilled 플래그가 true로 새고 이후 10128 주기 싱크에서 제외되어
// 클라 로컬 Painkillers가 스테일 상태로 남는다. 리스폰 직후 전 필드 0인 10128을 브로드캐스트해
// 클라 Apply가 GetOrAddComponent 후 5필드를 모두 0으로 덮어쓰게 한다 (제세동/Revive는 상태 유지).
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
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, in writer, (IEnumerable<knetid>)ServerMain.AllClientIdsExceptHost);
    }
}

// 리스폰/리바이브 시 usedNeuralBooster 초기화.
// MP 리스폰은 바디를 재사용하지만 ResetHealth가 usedNeuralBooster를 초기화하지 않아, 리스폰 후
// neuralbooster 재투약 시 바닐라 2회차 치명 부작용이 즉시 발동해 사망한다.
// ResetHealth Postfix에서 플래그를 false로 만들면 이후 10127 패킷에 실려 클라 값도 보정된다(클라 배포 불필요).
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
        body.usedNeuralBooster = false;
    }
}
