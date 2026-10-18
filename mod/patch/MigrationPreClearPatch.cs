using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using Together;

namespace CasuMod.Patch;

// SWAP 이전에, 출발지 인스턴스가 마이그레이션 대상 클라이언트에게만 "모든 엔티티
// 파괴" RPC(id=3, Destroy)를 enqueue 한다. 클라이언트는 이 RPC 를 처리하며
// ClientEntityManager.EntitiesDict 를 비우고, 이후 도착지의 첫 baseline 이 빈 사전에
// 적용되어 ServerStateData.ExecuteRpcs 의 IndexOutOfRange 크래시 없이 월드를 넘겨받는다.
//
// 근거(정적 분석):
//  - ServerEntityManager.AddRemoteCall(forPlayer != null) 는 해당 플레이어에게만 RPC 를
//    enqueue 한다 (LiteEntitySystem.decompiled.cs:4328).
//  - InternalRPCType.Destroy = 3 (동 7326), ExecuteFlags.SendToAll = 3,
//    IsRPCDisabled(sv, SendToAll) 는 항상 false (동 5720) 라 sync-group 필터에 걸리지 않는다.
//  - RPC 는 클라이언트 ack(LatestServerTick) 까지 매 send tick 재전송된다 (sem.cs:3913
//    -> NetPlayer.RemoveOldRpcs), 비신뢰 패킷이라도 도달성이 확보된다.
//  - 클라이언트는 Destroy RPC 처리 시 EntitiesDict[id]=null 로 제거한다 (cem.cs:638-648).
//  - SyncBody.OnDestroy -> NetBody.DestroyNPC 는 로컬 플레이어 GameObject 를 파괴하지
//    않는다 (NetBody.cs:929 if(!npc.IsBodyLocal())). 따라서 로컬 몸은 유지된다.
//
// 제약: PlayerResyncTimeout = 4s (sem.cs:46) 이내에 pre-clear + SWAP 이 끝나야 한다
// (그렇지 않으면 출발지가 재-baseline 을 보내 파괴한 엔티티를 되살린다). (서버측 전용)
internal static class MigrationPreClearPatch
{
    // InternalRPCType.Destroy
    private const ushort DestroyRpcId = 3;

    private static PropertyInfo _serverManagerProp;
    private static MethodInfo _addRemoteCall;

    // 대상 클라이언트가 가진 모든 엔티티에 파괴 RPC 를 enqueue 한다.
    internal static void Run(knetid clientId)
    {
        if (!EnsureReflection()) return;

        ServerEntityManager mgr = _serverManagerProp.GetValue(null) as ServerEntityManager;
        if (mgr == null) return;

        // NetPlayer.Id 는 LES 소유자 id == (byte)knetid (ScavEntityTypesMap.cs:35).
        NetPlayer player = mgr.GetPlayer((byte)(ushort)clientId);
        if (player == null) return;

        // AddRemoteCall(NetPlayer, InternalEntity, ushort, ExecuteFlags)
        var args = new object[4];
        foreach (InternalEntity entity in mgr.GetEntities<InternalEntity>())
        {
            if (entity == null || entity.IsRemoved) continue;
            args[0] = player;
            args[1] = entity;
            args[2] = DestroyRpcId;
            args[3] = ExecuteFlags.SendToAll;
            _addRemoteCall.Invoke(mgr, args);
        }
    }

    private static bool EnsureReflection()
    {
        if (_addRemoteCall != null) return true;
        _serverManagerProp = AccessTools.Property(typeof(Net), "ServerEntityManager");
        _addRemoteCall = AccessTools.Method(typeof(ServerEntityManager), "AddRemoteCall",
            new[] { typeof(NetPlayer), typeof(InternalEntity), typeof(ushort), typeof(ExecuteFlags) });
        if (_serverManagerProp == null || _addRemoteCall == null)
        {
            Plugin.Log.LogWarning("[Migration] pre-clear: reflection 실패 (ServerEntityManager/AddRemoteCall).");
            return false;
        }
        return true;
    }
}
