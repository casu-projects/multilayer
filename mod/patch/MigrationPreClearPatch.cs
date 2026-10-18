using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using Together;

namespace CasuMod.Patch;

// SWAP 직전, 출발지 인스턴스가 마이그레이션 대상 클라이언트에게만 "모든 엔티티 파괴" RPC(id=3, Destroy)를
// enqueue한다. 클라이언트는 처리 시 EntitiesDict에서 엔티티를 제거하고, 이후 도착지 첫 baseline이
// 빈 사전에 적용되어 ExecuteRpcs IndexOutOfRange 크래시 없이 월드를 넘겨받는다.
// 제약: RPC는 클라 ack까지 재전송되지만 비신뢰 패킷이므로 2차 버스트로 도달을 보장한다.
// PlayerResyncTimeout(4s) 이내에 pre-clear + SWAP이 끝나야 재-baseline 부활을 막을 수 있다.
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

        // NetPlayer.Id 는 LES 소유자 id == (byte)knetid.
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
