using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMod.Patch;

[HarmonyPatch]
internal static class SleepHealthSyncOwnerFilterPatch
{
    private static readonly Type MedicalSyncType =
        AccessTools.TypeByName("Together.MedicalSync");

    private static readonly MethodInfo CanSyncHealthMethod =
        AccessTools.Method(MedicalSyncType, "CanSyncHealth");

    private static MethodBase TargetMethod() =>
        AccessTools.Method(MedicalSyncType, "Server_SendCharacterHealth");

    private static bool Prefix(NetBody nb, bool force)
    {
        if (nb == null
            || !nb.IsPlayer
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
            if (clientId != nb.plr.playerId)
            {
                targets.Add(clientId);
            }
        }

        if (targets.Count == 0) return false;

        NetDataWriter writer = Net.CreateWriter((Enum)NetmsgId.CLIENT_HealthSync);
        writer.Put((ushort)nb.netId);
        var packet = new CharacterHealthStateSyncPacket();
        packet.Write(nb.body);
        MyLiteNetLibExtensions.Put(writer, packet);
        writer.Put(false);
        writer.CompressWriter();
        Net.Server_SendToClients(DeliveryMethod.Unreliable, in writer, targets);
        return false;
    }
}
