using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod.Patch;

// 수면 배속 중에는 서버의 주기적 체력 스냅샷을 수면 중인 유저에게 되돌려 보내지 않는다
// 서버/클라이언트가 모두 수면 게이지를 진행시키며 약 0.9초마다 서버 값이 로컬에 덮어써지는데,
// 렉/핑이 튀면 수면 게이지와 잠 깸이 롤백되는 원인이 된다
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
        if (nb == null
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
