using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod;

/// <summary>
/// 수면 배속 중에는 서버의 주기적 체력 스냅샷을 수면중인 유저에게 되돌려 보내지 않게함.
///
/// 멀티 모드는 서버와 클라이언트가 모두 수면 게이지를 진행시키면서, 약 0.9초마다 서버의 수면 게이지를 로컬 캐릭터에도 조건 없이 덮어씌우는데
/// 렉이 생기거나 핑이 튀면 수면 게이지 혹은 잠에서 깨는 현상이 롤백되면서 되돌아가는 이유에요.
/// </summary>
[HarmonyPatch]
internal static class SleepHealthSyncOwnerFilterPatch
{
    private static readonly Type MedicalSyncType =
        AccessTools.TypeByName("KrokoshaCasualtiesMP.MedicalSync");

    private static readonly MethodInfo CanSyncHealthMethod =
        AccessTools.Method(MedicalSyncType, "CanSyncHealth");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(MedicalSyncType, "Server_SendCharacterHealth");

    private static bool Prefix(NetBody nb, bool force)
    {
        if (!KrokoshaScavMultiplayer.is_dedicated_server
            || nb == null
            || !nb.is_player
            || nb.plr == null
            || nb.body == null
            || !nb.body.sleeping
            || !ServerMain.CheckIfEveryoneIsSleeping())
        {
            return true;
        }

        if (!force && !(bool)CanSyncHealthMethod.Invoke(null, null)) return false;

        var targets = new List<knetid>();
        foreach (knetid clientId in ServerMain.AllClientIdsExceptHost)
        {
            if (clientId != nb.plr.clientId)
            {
                targets.Add(clientId);
            }
        }

        if (targets.Count == 0) return false;

        NetDataWriter writer = Net.CreateWriter(10127);
        writer.Put((ushort)nb.netId);
        MyLiteNetLibExtensions.Put(writer, new CharacterHealthStateSyncPacket(nb.body));
        writer.Put(false);
        writer.CompressWriter();
        Net.Server_SendToClients(DeliveryMethod.Unreliable, in writer, targets);
        return false;
    }
}
