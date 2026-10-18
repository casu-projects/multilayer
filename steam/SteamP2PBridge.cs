using System.Runtime.InteropServices;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;

namespace CasuMpSteam;

public sealed class SteamP2PBridge : INetEventListener
{
    private static readonly TimeSpan PendingCloseDelay = TimeSpan.FromSeconds(1.5);
    private const uint BridgeMagic = 0x43534D50;
    private const byte BridgeVersion = 1;
    private const int MaxPendingPackets = 256;

    private readonly SteamConfig _config;
    private readonly SteamLobbyHost _lobby;
    private readonly string _bridgeHost;
    private readonly int _bridgePort;
    private readonly NetManager _bridgeManager;

    private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusChanged;
    private HSteamListenSocket _listenSocket;
    private bool _initialized;

    private readonly HashSet<HSteamNetConnection> _activeConnections = new();
    private readonly Dictionary<HSteamNetConnection, ulong> _connectionSteamIds = new();
    private readonly Dictionary<HSteamNetConnection, BridgeSession> _sessionsByConnection = new();
    private readonly Dictionary<NetPeer, BridgeSession> _sessionsByPeer = new();
    private readonly List<(HSteamNetConnection Conn, DateTime CloseAtUtc)> _pendingCloses = new();

    private sealed class BridgeSession
    {
        public required HSteamNetConnection Connection { get; init; }
        public required ulong SteamId { get; init; }
        public required string Name { get; init; }
        public byte[]? Intro { get; set; }
        public NetPeer? Peer { get; set; }
        public readonly List<byte[]> Pending = new();
        public bool Connected { get; set; }
    }

    public SteamP2PBridge(SteamConfig config, SteamLobbyHost lobby)
    {
        _config = config;
        _lobby = lobby;
        (_bridgeHost, _bridgePort) = SplitAddr(config.CoreBridgeAddr);
        _bridgeManager = new NetManager(this) { UnconnectedMessagesEnabled = false };
    }

    public bool Initialized => _initialized;

    public int BridgeCount
    {
        get { lock (_sessionsByConnection) return _sessionsByConnection.Count; }
    }

    public void Start()
    {
        try
        {
            ESteamAPIInitResult initResult = SteamAPI.InitEx(out string initErrMsg);
            if (initResult != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                SteamLog.Info($"SteamAPI.InitEx() 실패: {initResult} — {initErrMsg} "
                    + "Steam P2P는 비활성화됩니다.");
                return;
            }

            _initialized = true;
            SteamLog.Info("SteamAPI.InitEx() 성공.");
            SteamNetworkingUtils.InitRelayNetworkAccess();
            _connectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            _bridgeManager.Start();
        }
        catch (DllNotFoundException ex)
        {
            SteamLog.Info($"Steam P2P 비활성화: {ex.Message}");
        }
        catch (Exception ex)
        {
            SteamLog.Info($"Steam P2P 비활성화: {ex.Message}");
        }
    }

    public void PollEvents()
    {
        if (!_initialized) return;

        _bridgeManager.PollEvents();
        SteamAPI.RunCallbacks();

        var buffer = new IntPtr[64];
        foreach (HSteamNetConnection conn in _activeConnections.ToList())
        {
            bool more;
            do
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, buffer, buffer.Length);
                for (int i = 0; i < count; i++)
                {
                    HandleIncomingMessage(conn, buffer[i]);
                }
                more = count == buffer.Length;
            }
            while (more);
        }

        if (_pendingCloses.Count > 0)
        {
            DateTime now = DateTime.UtcNow;
            List<HSteamNetConnection> due = _pendingCloses
                .Where(p => now >= p.CloseAtUtc)
                .Select(p => p.Conn)
                .ToList();
            foreach (HSteamNetConnection conn in due)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "handshake rejected", bEnableLinger: false);
            }
            _pendingCloses.RemoveAll(p => due.Contains(p.Conn));
        }
    }

    public void Stop()
    {
        foreach (BridgeSession session in _sessionsByConnection.Values.ToList())
        {
            if (session.Peer != null) _bridgeManager.DisconnectPeer(session.Peer);
        }
        _sessionsByConnection.Clear();
        _sessionsByPeer.Clear();
        _pendingCloses.Clear();
        _bridgeManager.Stop();

        if (_initialized)
        {
            foreach (HSteamNetConnection conn in _activeConnections.ToList())
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "Server shutting down", bEnableLinger: false);
            }
            _activeConnections.Clear();
            _connectionSteamIds.Clear();

            if (_listenSocket.m_HSteamListenSocket != 0)
            {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            }
            SteamAPI.Shutdown();
            _initialized = false;
        }
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
    {
        switch (callback.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
            {
                EResult result = SteamNetworkingSockets.AcceptConnection(callback.m_hConn);
                if (result == EResult.k_EResultOK)
                {
                    _activeConnections.Add(callback.m_hConn);
                    _connectionSteamIds[callback.m_hConn] = callback.m_info.m_identityRemote.GetSteamID64();
                }
                else
                {
                    SteamLog.Info($"P2P 연결 수락 실패: {result}");
                }
                break;
            }
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            {
                HSteamNetConnection conn = callback.m_hConn;
                if (_activeConnections.Remove(conn))
                {
                    _connectionSteamIds.Remove(conn);
                    if (_sessionsByConnection.Remove(conn, out BridgeSession? session))
                    {
                        if (session.Peer != null)
                        {
                            _sessionsByPeer.Remove(session.Peer);
                            _bridgeManager.DisconnectPeer(session.Peer);
                        }
                    }
                    SteamNetworkingSockets.CloseConnection(conn, 0, null, bEnableLinger: false);
                }
                break;
            }
        }
    }

    private void HandleIncomingMessage(HSteamNetConnection conn, IntPtr messagePtr)
    {
        try
        {
            SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(messagePtr);
            if (message.m_cbSize <= 0) return;

            var payload = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, payload, 0, payload.Length);

            if (_sessionsByConnection.TryGetValue(conn, out BridgeSession? session))
            {
                if (session.Connected && session.Peer != null)
                {
                    session.Peer.Send(payload, 0, DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    if (session.Pending.Count >= MaxPendingPackets) session.Pending.RemoveAt(0);
                    session.Pending.Add(payload);
                }
                return;
            }

            _connectionSteamIds.TryGetValue(conn, out ulong steamId);
            if (steamId == 0) return;

            if (!_bridgeManager.IsRunning)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "bridge unavailable", bEnableLinger: false);
                return;
            }

            string name = SteamNameResolver.ResolveName(steamId) ?? "";
            var writer = new NetDataWriter();
            writer.Put(BridgeMagic);
            writer.Put(BridgeVersion);
            writer.Put(_config.BridgeSecret);
            writer.Put(steamId);
            writer.Put(name);

            NetPeer peer = _bridgeManager.Connect(_bridgeHost, _bridgePort, writer);
            var newSession = new BridgeSession
            {
                Connection = conn,
                SteamId = steamId,
                Name = name,
                Intro = payload,
                Peer = peer,
            };
            _sessionsByConnection[conn] = newSession;
            _sessionsByPeer[peer] = newSession;
        }
        finally
        {
            SteamNetworkingMessage_t.Release(messagePtr);
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        if (!_sessionsByPeer.TryGetValue(peer, out BridgeSession? session)) return;
        session.Connected = true;
        if (session.Intro != null)
        {
            peer.Send(session.Intro, 0, DeliveryMethod.ReliableOrdered);
            session.Intro = null;
        }
        foreach (byte[] data in session.Pending)
        {
            peer.Send(data, 0, DeliveryMethod.ReliableOrdered);
        }
        session.Pending.Clear();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        byte[] data = reader.GetRemainingBytes();
        reader.Recycle();

        if (!_sessionsByPeer.TryGetValue(peer, out BridgeSession? session)) return;
        if (!_activeConnections.Contains(session.Connection)) return;

        int sendFlag = ConvertDeliveryMethodToSteamSendFlag(deliveryMethod);
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            EResult result = SteamNetworkingSockets.SendMessageToConnection(
                session.Connection, ptr, (uint)data.Length, sendFlag, out long msgNumber);
            if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
            {
                SteamLog.Info($"SendMessageToConnection 실패: {result} "
                    + $"(conn={session.Connection.m_HSteamNetConnection}, {data.Length}바이트, msgNumber={msgNumber}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!_sessionsByPeer.Remove(peer, out BridgeSession? session)) return;

        _sessionsByConnection.Remove(session.Connection);
        _activeConnections.Remove(session.Connection);
        _connectionSteamIds.Remove(session.Connection);

        string reason = "";
        try { disconnectInfo.AdditionalData.TryGetString(out reason); } catch { reason = ""; }

        if (!string.IsNullOrEmpty(reason))
        {
            _lobby.SendKickChatMessage(session.SteamId, reason);
        }
        _pendingCloses.Add((session.Connection, DateTime.UtcNow + PendingCloseDelay));
    }

    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    private static int ConvertDeliveryMethodToSteamSendFlag(DeliveryMethod method) => method switch
    {
        DeliveryMethod.ReliableUnordered => 9,
        DeliveryMethod.ReliableOrdered => 9,
        DeliveryMethod.ReliableSequenced => 9,
        DeliveryMethod.Sequenced => 1,
        _ => 5,
    };

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        if (idx <= 0) throw new InvalidOperationException($"잘못된 CoreBridgeAddr: {addr}");
        return (addr[..idx], int.Parse(addr[(idx + 1)..]));
    }
}
