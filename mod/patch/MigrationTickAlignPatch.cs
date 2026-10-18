using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using LiteNetLib.Utils;
using Together;

namespace CasuMod.Patch;

// 마이그레이션 도착 클라는 출발지의 ServerTick을 학습한 상태로 입력을 보낸다(StateA = 출발지 틱).
// 도착지 tick이 뒤처져 있으면 ServerEntityManager.Deserialize의 게이트
// (SequenceDiff(StateA, base.Tick) > 0 || StateB > base.Tick)가 모든 입력을 "invalid sequence"로
// 거부해 플레이어가 Active로 전환되지 못하고 WorldPlacePlayer에서 무한 대기한다.
// 도착 후 첫 입력에서 StateA/StateB를 파싱해 도착지 tick을 그보다 앞서도록 정렬한다.
[HarmonyPatch(typeof(ScavEntityTypesMap), "ServerReceiver__LiteEntitySystem")]
internal static class MigrationTickAlignPatch
{
    private static readonly HashSet<knetid> Aligned = new();

    private static PropertyInfo _serverManagerProp;
    private static FieldInfo _tickField;

    // 리더는 메시지 id(224) 1바이트를 이미 소비한 상태로 핸들러에 들어온다.
    // 다음 바이트가 LES 패킷 타입이고, 그 뒤 FirstInputHeader(4) + InputPacketHeader(StateA,StateB,...) 순.
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

        _tickField.SetValue(mgr, desired);
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
