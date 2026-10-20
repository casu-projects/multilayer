using System;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpServer;

public sealed class ClientSession : INetEventListener
{
    private static long _nextSessionId;
    private const int MaxPendingReliablePackets = 256;
    private static readonly TimeSpan BackendRetryInterval = TimeSpan.FromSeconds(0.5);
    private const int BackendMaxRetries = 600;

    private readonly IClientSink _clientSink;
    private readonly byte[] _intro;
    private readonly ClientGateway _core;

    private NetPeer? _backendPeer;
    private NetManager _backendManager;

    private bool _swapping;
    private bool _hasEverConnectedToBackend;
    private int _connectRetryCount;
    private DateTime? _nextRetryAtUtc;

    private readonly List<(byte[] Data, byte Channel, DeliveryMethod Method)> _pendingFromClient = new();

    public long SessionId { get; } = Interlocked.Increment(ref _nextSessionId);
    public string Transport { get; }
    public PlayerKey Player { get; }
    public string Username { get; }
    public ulong? SteamId { get; }

    public string Password { get; }
    public SessionState State { get; private set; }
    public string? InstanceId { get; private set; }
    public string BackendAddr { get; private set; } = "";
    public bool Disposed { get; private set; }

    public ushort ForcedClientId { get; private set; }
    public bool IsReturningPlayer { get; private set; }
    public bool IsMigratingArrival { get; private set; }

    public bool HasEverConnectedToBackend => _hasEverConnectedToBackend;

    private readonly string? _tailName;

    public DateTime? RoutingWaitStartedAt { get; set; }

    internal ClientSession(IClientSink clientSink, byte[] intro, PlayerKey player,
        string steamName, ulong? steamId, ClientGateway core, string transport)
    {
        _clientSink = clientSink;
        _intro = intro;
        Player = player;
        _core = core;
        HandshakeReader.TryParseCredentials(intro, out string introUsername, out string pw);
        Username = !string.IsNullOrEmpty(introUsername) ? introUsername : steamName;
        Password = pw;
        SteamId = steamId;
        Transport = transport;
        State = SessionState.Accepted;
        _backendManager = new NetManager(this) { UnconnectedMessagesEnabled = false };

        if (IsBrokenName(Username) && !string.IsNullOrEmpty(steamName))
        {
            Username = steamName;
            _tailName = steamName;
        }
    }

    private static bool IsBrokenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        foreach (char c in name)
        {
            if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }

    public void EnterRoutingWait()
    {
        State = SessionState.Routing;
        RoutingWaitStartedAt = DateTime.UtcNow;
    }

    public void BeginRoute(string backendAddr, ushort clientId, bool isReturning, string? instanceId)
    {
        BackendAddr = backendAddr;
        ForcedClientId = clientId;
        IsReturningPlayer = isReturning;
        InstanceId = instanceId;
        RoutingWaitStartedAt = null;
        _nextRetryAtUtc = null;   // 재라우팅 시 기존 재시도 예약 취소
        ConnectToBackend(backendAddr);
    }

    public void SwapBackend(string backendAddr, string? instanceId)
    {
        string previousBackend = BackendAddr;
        IsReturningPlayer = true;
        IsMigratingArrival = true;
        InstanceId = instanceId;

        _swapping = true;
        _backendManager.PollEvents();
        _backendManager.Stop();
        _pendingFromClient.Clear();

        BackendAddr = backendAddr;
        _hasEverConnectedToBackend = false;
        _connectRetryCount = 0;
        _nextRetryAtUtc = null;
        State = SessionState.Swapping; // 스왑 중 클라 패킷 드랍
        ConnectToBackend(backendAddr);
        _swapping = false;
    }

    public void ForwardFromClient(byte[] data, byte channel, DeliveryMethod method)
    {
        if (_backendPeer != null && _backendPeer.ConnectionState == ConnectionState.Connected)
        {
            _backendPeer.Send(data, channel, method);
        }
        else if (method is DeliveryMethod.ReliableOrdered
            or DeliveryMethod.ReliableUnordered
            or DeliveryMethod.ReliableSequenced)
        {
            if (_pendingFromClient.Count >= MaxPendingReliablePackets)
            {
                _pendingFromClient.RemoveAt(0);
            }
            _pendingFromClient.Add((data, channel, method)); // 백엔드 미연결 시 신뢰 패킷 버퍼
        }
    }

    public void Kick(string reason)
    {
        _clientSink.DisconnectClient(reason);
        Dispose();
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        State = SessionState.Closed; // 세션 종료
        _backendManager.Stop();
    }

    public void Tick()
    {
        _backendManager.PollEvents();
        if (_nextRetryAtUtc.HasValue && DateTime.UtcNow >= _nextRetryAtUtc.Value)
        {
            _nextRetryAtUtc = null;
            if (!Disposed && State is SessionState.Connecting or SessionState.Swapping)
            {
                ConnectToBackend(BackendAddr);
            }
        }
    }

    private void ConnectToBackend(string addr)
    {
        State = SessionState.Connecting;
        if (!ClientGateway.IsUsableBackendAddr(addr))
        {
            _nextRetryAtUtc = DateTime.UtcNow + BackendRetryInterval;
            return;
        }
        try
        {
            var (host, port) = SplitAddr(addr);
            _backendManager.PollEvents();
            if (State == SessionState.Active && _backendPeer != null
                && _backendPeer.ConnectionState == ConnectionState.Connected)
            {
                return;
            }
            _backendManager.Stop();   // 이전 매니저 정리 (재라우팅/재시도 누수 방지)
            var manager = new NetManager(this) { UnconnectedMessagesEnabled = false };
            manager.Start();
            NetDataWriter connectData = TailV2.BuildConnectData(
                _intro, SteamId ?? 0UL, ForcedClientId, IsReturningPlayer, IsMigratingArrival, _tailName);
            _backendPeer = manager.Connect(host, port, connectData);
            _backendManager = manager;
        }
        catch (Exception ex)
        {
            Log.Info($"{Username} 백엔드 연결 실패({addr}): {ex.Message}");
            _core.OnSessionBackendFailed(this, "Backend connection failed.");
        }
    }

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        return (addr[..idx], int.Parse(addr[(idx + 1)..]));
    }

    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

    public void OnPeerConnected(NetPeer peer)
    {
        _hasEverConnectedToBackend = true;
        _connectRetryCount = 0;
        State = SessionState.Active;
        foreach ((byte[] data, byte channel, DeliveryMethod method) in _pendingFromClient)
        {
            peer.Send(data, channel, method);
        }
        _pendingFromClient.Clear();
        _core.OnSessionBackendConnected(this);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        byte[] data = reader.GetRemainingBytes();
        reader.Recycle();
        _clientSink.SendToClient(data, channelNumber, deliveryMethod);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (peer != _backendPeer) return;
        if (_swapping || Disposed) return;

        string rejectReason = "";
        if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected)
        {
            disconnectInfo.AdditionalData.TryGetString(out rejectReason);
        }

        bool isDefiniteRejection = disconnectInfo.Reason == DisconnectReason.ConnectionRejected;
        if (isDefiniteRejection && rejectReason.Contains("is generating world", StringComparison.OrdinalIgnoreCase))
        {
            isDefiniteRejection = false;
        }

        if (!isDefiniteRejection && !_hasEverConnectedToBackend && !Disposed
            && _connectRetryCount < BackendMaxRetries)
        {
            _connectRetryCount++;
            _nextRetryAtUtc = DateTime.UtcNow + BackendRetryInterval;
            return;
        }

        string finalReason = string.IsNullOrEmpty(rejectReason) ? "Backend connection closed." : rejectReason;
        Log.Info($"{Username} 연결 실패 (백엔드: {BackendAddr}, 사유: {finalReason})");
        _core.OnSessionBackendFailed(this, finalReason);
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
