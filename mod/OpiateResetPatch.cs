using System.Collections.Generic;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;

namespace CasuMod;

/// <summary>리스폰 시 opiate 상태 초기화 — Server_RespawnCharacter.
/// 바닐라 리스폰(Server_ReviveCharacter → ResetHealth)이 서버측에서 Painkillers
/// 컴포넌트를 Object.Destroy로 제거하지만, Unity 지연 파괴로 인해 같은 프레임에 전송되는
/// 10127 헬스 싱크의 painkilled 플래그가 true로 새며, 이후 10128 주기 싱크는 서버측
/// 컴포넌트가 사라져 대상에서 제외된다. 그 결과 클라이언트 로컬 Painkillers 컴포넌트가
/// 스테일 상태(중독/내성/수용치)로 영원히 남는다.
/// 해결: 리스폰 직후 전 필드가 0인 10128 패킷을 브로드캐스트 — 클라이언트 Apply가
/// GetOrAddComponent + 5필드 전체를 0으로 덮어써 완전 리셋한다. (제세동/ReviveCharacter는
/// 상태를 유지하는 것이 바닐라 의미이므로 대상에서 제외)</summary>
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