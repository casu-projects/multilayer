using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpGateway;

// 직접연결(DirectIpAdapter) - LiteNetLib 리스너 . 클라이언트 피어 -> 세션 매핑을
// 어댑터가 소유한다. 신원은 username .
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

 // 인원 제한 - 접속 시도 단계에서 차단 (AUTH_INFO로 수신한 최대 인원 기준)
        if (_core.SessionCount >= _core.MaxPlayers)
        {
            Reject(request, "Server is full.");
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

 // 서버 비밀번호 검증 (오케스트레이터 AUTH_INFO 사본 - 조기 거부, 게임이 최종 검증).
 // 파싱 실패로 password가 비어도 게임 서버가 다시 검증하므로 안전.
        if (!_core.ValidatePassword(password))
        {
            Reject(request, "Wrong password.");
            return;
        }

        PlayerKey player = PlayerKey.FromUsername(username);
        NetPeer peer = request.Accept();

        var session = new ClientSession(
            new LiteNetLibClientSink(peer), raw, player, username, steamId: null, _core,
            transport: "Direct");
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
