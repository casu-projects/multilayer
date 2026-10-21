using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpServer;

public sealed class DirectIpAdapter : INetEventListener
{
    private readonly ServerConfig _config;
    private readonly ClientGateway _core;
    private readonly NetManager _listenManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessionsByPeer = new();

    public DirectIpAdapter(ServerConfig config, ClientGateway core)
    {
        _config = config;
        _core = core;
        _listenManager = new NetManager(this) { UnconnectedMessagesEnabled = false };
    }

    public void Start()
    {
        _listenManager.Start(_config.DirectListenPort);
        Log.Info($"리스너 시작: 포트 {_config.DirectListenPort}");
    }

    public void PollEvents() => _listenManager.PollEvents();

    public void Stop()
    {
        foreach (ClientSession session in _sessionsByPeer.Values.ToList())
        {
            _core.CloseSession(session, "gateway shutdown");
        }
        _sessionsByPeer.Clear();
        _listenManager.Stop();
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (_core.IsMaintenance)
        {
            Reject(request, _core.MaintenanceMessage.Length > 0 ? _core.MaintenanceMessage : "Server is under maintenance.");
            return;
        }

        if (_core.IsAdmissionBlocked)
        {
            Reject(request, _core.PrewarmGateMessage);
            return;
        }

        byte[] raw = request.Data.RawData[request.Data.UserDataOffset..(request.Data.UserDataOffset + request.Data.UserDataSize)];
        if (!HandshakeReader.TryParseCredentials(raw, out string username, out string password))
        {
            Log.Info($"핸드셰이크 파싱 실패 (길이 {raw.Length}): "
                + $"{BitConverter.ToString(raw)}");
            request.Reject();
            return;
        }

        if (!_core.ValidatePassword(password))
        {
            Reject(request, "Wrong password.");
            return;
        }

        PlayerKey player = PlayerKey.FromUsername(username);
        NetPeer peer = request.Accept();

        var session = new ClientSession(
            new LiteNetLibClientSink(peer), raw, player, steamName: "", steamId: null, _core,
            transport: "Direct");
        _sessionsByPeer[peer] = session;
        _core.AcceptSession(session);
        if (session.Disposed) _sessionsByPeer.Remove(peer);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        byte[] data = reader.GetRemainingBytes();
        reader.Recycle();
        if (_sessionsByPeer.TryGetValue(peer, out ClientSession? session))
        {
            session.ForwardFromClient(data, channelNumber, deliveryMethod);
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_sessionsByPeer.Remove(peer, out ClientSession? session))
        {
            _core.CloseSession(session, "client disconnected");
        }
    }

    private static void Reject(ConnectionRequest request, string reason)
    {
        var writer = new NetDataWriter();
        writer.Put(reason);
        request.Reject(writer);
    }

    public void OnPeerConnected(NetPeer peer) { }
    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
