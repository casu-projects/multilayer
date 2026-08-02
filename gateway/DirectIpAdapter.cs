using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpGateway;

/// <summary>직접연결(DirectIpAdapter) — LiteNetLib 리스너 (G2). 클라이언트 피어 → 세션 매핑을
/// 어댑터가 소유한다. 신원은 username (G1-5).</summary>
public sealed class DirectIpAdapter : INetEventListener
{
    private readonly GatewayConfig _config;
    private readonly GatewayCore _core;
    private readonly NetManager _listenManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessionsByPeer = new();

    public DirectIpAdapter(GatewayConfig config, GatewayCore core)
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

        byte[] raw = request.Data.RawData[request.Data.UserDataOffset..(request.Data.UserDataOffset + request.Data.UserDataSize)];
        if (!HandshakeReader.TryParseUsername(raw, out string username))
        {
            Log.Info($"핸드셰이크 파싱 실패 (길이 {raw.Length}): "
                + $"{BitConverter.ToString(raw)}");
            request.Reject();
            return;
        }

        PlayerKey player = PlayerKey.FromUsername(username);
        NetPeer peer = request.Accept();

        var session = new ClientSession(
            new LiteNetLibClientSink(peer), raw, player, username, steamId: null, _core);
        _sessionsByPeer[peer] = session;
        _core.AcceptSession(session);
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
