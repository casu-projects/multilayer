using LiteNetLib;
using LiteNetLib.Utils;
using System.Net;

namespace CasuMpServer;

public sealed class SteamBridgeAdapter : INetEventListener
{
    private const uint BridgeMagic = 0x43534D50;
    private const byte BridgeVersion = 1;
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(15);

    private readonly ServerConfig _config;
    private readonly ClientGateway _core;
    private readonly NetManager _listenManager;
    private readonly Dictionary<NetPeer, PendingBridge> _pending = new();
    private readonly Dictionary<NetPeer, ClientSession> _sessionsByPeer = new();

    private sealed class PendingBridge
    {
        public required ulong SteamId { get; init; }
        public required string Name { get; init; }
        public required DateTime AcceptedAtUtc { get; init; }
    }

    public SteamBridgeAdapter(ServerConfig config, ClientGateway core)
    {
        _config = config;
        _core = core;
        _listenManager = new NetManager(this) { UnconnectedMessagesEnabled = false };
    }

    public void Start()
    {
        try
        {
            bool started = _listenManager.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, _config.SteamBridgePort);
            if (started)
            {
                Log.Info($"Steam 브리지 리스너 시작: 127.0.0.1:{_config.SteamBridgePort}");
            }
            else
            {
                Log.Info($"Steam 브리지 리스너 시작 실패: 포트 {_config.SteamBridgePort}");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Steam 브리지 리스너 시작 실패: {ex.Message}");
        }
    }

    public void PollEvents()
    {
        if (_listenManager.IsRunning) _listenManager.PollEvents();
    }

    public void Tick()
    {
        if (!_listenManager.IsRunning) return;

        DateTime now = DateTime.UtcNow;
        foreach (var pair in _pending.ToList())
        {
            if (now - pair.Value.AcceptedAtUtc < PendingTimeout) continue;
            _pending.Remove(pair.Key);
            _listenManager.DisconnectPeer(pair.Key);
        }
    }

    public void Stop()
    {
        foreach (ClientSession session in _sessionsByPeer.Values.ToList())
        {
            _core.CloseSession(session, "gateway shutdown");
        }
        _sessionsByPeer.Clear();
        _pending.Clear();
        _listenManager.Stop();
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        byte[] raw = request.Data.RawData[request.Data.UserDataOffset..(request.Data.UserDataOffset + request.Data.UserDataSize)];
        var reader = new NetDataReader(raw);
        try
        {
            uint magic = reader.GetUInt();
            byte version = reader.GetByte();
            string secret = reader.GetString();
            ulong steamId = reader.GetULong();
            string name = reader.GetString();

            if (magic != BridgeMagic || version != BridgeVersion || steamId == 0)
            {
                Reject(request, "invalid bridge handshake");
                return;
            }
            if (!string.Equals(secret, _config.SteamBridgeSecret, StringComparison.Ordinal))
            {
                Reject(request, "invalid bridge secret");
                return;
            }

            NetPeer peer = request.Accept();
            _pending[peer] = new PendingBridge
            {
                SteamId = steamId,
                Name = name,
                AcceptedAtUtc = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            Log.Info($"Steam 브리지 핸드셰이크 실패: {ex.Message}");
            request.Reject();
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        byte[] data = reader.GetRemainingBytes();
        reader.Recycle();

        if (_sessionsByPeer.TryGetValue(peer, out ClientSession? session))
        {
            session.ForwardFromClient(data, channelNumber, deliveryMethod);
            return;
        }

        if (!_pending.Remove(peer, out PendingBridge? pending)) return;

        var newSession = new ClientSession(
            new LiteNetLibClientSink(peer), data, PlayerKey.FromSteamId(pending.SteamId),
            pending.Name, pending.SteamId, _core, transport: "Steam");
        _sessionsByPeer[peer] = newSession;
        _core.AcceptSession(newSession);
        if (newSession.Disposed) _sessionsByPeer.Remove(peer);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _pending.Remove(peer);
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
    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
