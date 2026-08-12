using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using LiteNetLib;

namespace CasuMod;

// 리스폰 시 opiate 상태 초기화 - Server_RespawnCharacter
// 바닐라 리스폰(Server_ReviveCharacter -> ResetHealth)이 서버측에서 Painkillers
// 컴포넌트를 Object.Destroy로 제거하지만, Unity 지연 파괴로 인해 같은 프레임에 전송되는
// 10127 헬스 싱크의 painkilled 플래그가 true로 새며, 이후 10128 주기 싱크는 서버측
// 컴포넌트가 사라져 대상에서 제외된다. 그 결과 클라이언트 로컬 Painkillers 컴포넌트가
// 스테일 상태(중독/내성/수용치)로 영원히 남는다
// 해결: 리스폰 직후 전 필드가 0인 10128 패킷을 브로드캐스트 - 클라이언트 Apply가
// GetOrAddComponent + 5필드 전체를 0으로 덮어써 완전 리셋한다. (제세동/ReviveCharacter는
// 상태를 유지하는 것이 바닐라 의미이므로 대상에서 제외)
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

// 리스폰/리바이브 시 usedNeuralBooster 초기화
// 배경: MP 리스폰(Server_RespawnCharacter)이 바디를 재사용하는데 MP 자체 ResetHealth
// (Util_BodyExtensions:128-234)가 usedNeuralBooster를 초기화하지 않는다 - 사망 전 1회 사용
// 플래그가 TRUE로 잔존하고, 10127 헬스 싱크(L417/L518)가 이를 클라에도 유지시킨다. 리스폰 후
// neuralbooster를 재투약하면 바닐라의 "2회차 치명 부작용"(Item.cs:1133 - sickness+100/뇌-30/
// 내출혈+100/양안 제거/전 림브 근육 20%)이 즉시 발동해 사망한다
// 수정: ResetHealth Postfix에서 플래그를 false로 - Server_ReviveCharacter가 ResetHealth(L1121)
// 이후 10127을 발신(L1124)하므로 패킷에 false가 실려 클라 로컬 값도 자동 보정된다 (클라 배포
// 불필요). 리스폰·CPR/제세동 등 모든 ResetHealth 경로에 일괄 적용 - "완전 리셋" 의미와 일치
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
