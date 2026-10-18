using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib.Utils;
using Together;

namespace CasuMod.Patch;

[HarmonyPatch(typeof(ScavEntityTypesMap), "ServerReceiver__LiteEntitySystem")]
internal static class MigrationTickAlignPatch
{
    private static readonly HashSet<knetid> Aligned = new();

    private static PropertyInfo _serverManagerProp;
    private static FieldInfo _tickField;

    private const byte LtInputPacket = 2;

    private static void Prefix(knetid clientId, NetDataReader reader)
    {
        if (!MigrationArrivalTracker.ClientIds.Contains(clientId)) return;
        if (Aligned.Contains(clientId)) return;

        EnsureReflection();
        ServerEntityManager mgr = _serverManagerProp.GetValue(null) as ServerEntityManager;
        if (mgr == null) return;

        if (!TryReadStates(reader, out ushort stateA, out ushort stateB)) return;

        ushort tick = mgr.Tick;
        ushort desired = tick;
        if (LiteEntitySystem.Utils.SequenceDiff(stateA, desired) > 0) desired = stateA;
        if (LiteEntitySystem.Utils.SequenceDiff(stateB, desired) > 0) desired = stateB;

        Aligned.Add(clientId);

        if (desired == tick) return;

        _tickField.SetValue(mgr, desired); // 도착지 tick을 클라 StateA/B보다 앞서도록
    }

    private static bool TryReadStates(NetDataReader reader, out ushort stateA, out ushort stateB)
    {
        stateA = 0;
        stateB = 0;
        byte[] data = reader.RawData;
        int p = reader.Position;
        if (data == null || p < 0 || p + 9 > data.Length) return false;
        if (data[p] != LtInputPacket) return false;
        p += 1 + 4; // packet type + FirstInputHeader(Tick, LatestServerTick)
        stateA = (ushort)(data[p] | (data[p + 1] << 8));
        stateB = (ushort)(data[p + 2] | (data[p + 3] << 8));
        return true;
    }

    private static void EnsureReflection()
    {
        if (_serverManagerProp != null && _tickField != null) return;
        _serverManagerProp = AccessTools.Property(typeof(Net), "ServerEntityManager");
        _tickField = AccessTools.Field(typeof(EntityManager), "_tick");
    }

    internal static void Reset(knetid clientId) => Aligned.Remove(clientId);
}
