using System.Reflection;
using HarmonyLib;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using Together;

namespace CasuMod.Patch;

internal static class MigrationPreClearPatch
{
    private const ushort DestroyRpcId = 3;

    private static PropertyInfo _serverManagerProp;
    private static MethodInfo _addRemoteCall;

    internal static void Run(knetid clientId)
    {
        if (!EnsureReflection()) return;

        ServerEntityManager mgr = _serverManagerProp.GetValue(null) as ServerEntityManager;
        if (mgr == null) return;

        NetPlayer player = mgr.GetPlayer((byte)(ushort)clientId);
        if (player == null) return;

        var args = new object[4];
        foreach (InternalEntity entity in mgr.GetEntities<InternalEntity>())
        {
            if (entity == null || entity.IsRemoved) continue;
            args[0] = player;
            args[1] = entity;
            args[2] = DestroyRpcId;
            args[3] = ExecuteFlags.SendToAll;
            _addRemoteCall.Invoke(mgr, args); // 대상 클라에 Destroy RPC enqueue
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
