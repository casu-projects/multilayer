using System.Runtime.InteropServices;
using LiteNetLib;
using Steamworks;

namespace CasuMpGateway;

public sealed class SteamLobbyAdapter
{
    private static readonly TimeSpan PendingCloseDelay = TimeSpan.FromSeconds(1.5);

    private readonly GatewayConfig _config;
    private readonly GatewayCore _core;
    private readonly SteamLobby _lobby;

    private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusChanged;
    private HSteamListenSocket _listenSocket;
    private bool _initialized;

    private readonly HashSet<HSteamNetConnection> _activeConnections = new();
    private readonly Dictionary<HSteamNetConnection, ulong> _connectionSteamIds = new();
    private readonly Dictionary<HSteamNetConnection, ClientSession> _sessionsByConnection = new();
    private readonly List<(HSteamNetConnection Conn, DateTime CloseAtUtc)> _pendingCloses = new();

    public SteamLobbyAdapter(GatewayConfig config, GatewayCore core)
    {
        _config = config;
        _core = core;
        _lobby = new SteamLobby(config.SteamSessionPath, config.SteamVersionTag, core, config.Motd);
    }

    public void Start()
    {
        try
        {
            ESteamAPIInitResult initResult = SteamAPI.InitEx(out string initErrMsg);
            if (initResult != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Logger.Info($"SteamAPI.InitEx() 실패: {initResult} — {initErrMsg} "
                    + "Steam P2P는 비활성화됩니다.");
            }
            else
            {
                _initialized = true;
                Logger.Info("SteamAPI.InitEx() 성공.");

                SteamNetworkingUtils.InitRelayNetworkAccess();
                _connectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            }
        }
        catch (DllNotFoundException ex)
        {
            Logger.Info($"Steam P2P 비활성화: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Info($"Steam P2P 비활성화: {ex.Message}");
        }

        _lobby.Start();
    }

    public void PollEvents()
    {
        _lobby.Tick();
        if (!_initialized)
        {
            return;
        }

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
            List<HSteamNetConnection> due = _pendingCloses.Where(p => now >= p.CloseAtUtc).Select(p => p.Conn).ToList();
            foreach (HSteamNetConnection conn in due)
            {
                if (!_sessionsByConnection.ContainsKey(conn))
                {
                    SteamNetworkingSockets.CloseConnection(conn, 0, "handshake rejected", bEnableLinger: false);
                }
            }
            _pendingCloses.RemoveAll(p => due.Contains(p.Conn));
        }
    }

    public void Stop()
    {
        if (_initialized)
        {
            foreach (HSteamNetConnection conn in _activeConnections)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "Server shutting down", bEnableLinger: false);
            }
            _activeConnections.Clear();
            _connectionSteamIds.Clear();
            _sessionsByConnection.Clear();
            _pendingCloses.Clear();

            if (_listenSocket.m_HSteamListenSocket != 0)
            {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
            }
            SteamAPI.Shutdown();
            _initialized = false;
        }
        _lobby.Stop();
    }

    public void UpdateLobbyMetadata(GatewayCore.LobbyMetadataPayload payload) => _lobby.UpdateDynamicMetadata(payload);

    internal void RequestClose(HSteamNetConnection conn, ulong steamId, string reason)
    {
        if (steamId != 0)
        {
            _lobby.SendKickChatMessage(steamId, reason);
        }
        _pendingCloses.Add((conn, DateTime.UtcNow + PendingCloseDelay));
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
                    Logger.Info($"P2P 연결 수락 실패: {result}");
                }
                break;
            }
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
            {
                break;
            }
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            {
                HSteamNetConnection conn = callback.m_hConn;
                if (_activeConnections.Remove(conn))
                {
                    _connectionSteamIds.Remove(conn);
                    if (_sessionsByConnection.Remove(conn, out ClientSession? session))
                    {
                        _core.CloseSession(session, "client disconnected");
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
            if (message.m_cbSize <= 0)
            {
                return;
            }
            
            int payloadLength = message.m_cbSize;
            var payload = new byte[payloadLength];
            Marshal.Copy(message.m_pData, payload, 0, payloadLength);
            const DeliveryMethod method = DeliveryMethod.ReliableOrdered;

            if (_sessionsByConnection.TryGetValue(conn, out ClientSession? session))
            {
                session.ForwardFromClient(payload, channel: 0, method);
            }
            else
            {
                _connectionSteamIds.TryGetValue(conn, out ulong steamId64);
                var newSession = new ClientSession(
                    new SteamClientSink(this, conn, steamId64),
                    payload,
                    PlayerKey.FromSteamId(steamId64),
                    username: "", steamId: steamId64, _core,
                    transport: "Steam");
                _core.AcceptSession(newSession);
                if (newSession.Disposed)
                {
                    _pendingCloses.Add((conn, DateTime.UtcNow + PendingCloseDelay));
                    _activeConnections.Remove(conn);
                }
                else
                {
                    _sessionsByConnection[conn] = newSession;
                }
            }
        }
        finally
        {
            SteamNetworkingMessage_t.Release(messagePtr);
        }
    }
}

public sealed class SteamClientSink : IClientSink
{
    private readonly SteamLobbyAdapter _adapter;
    private readonly HSteamNetConnection _connection;
    private readonly ulong _steamId;

    internal SteamClientSink(SteamLobbyAdapter adapter, HSteamNetConnection connection, ulong steamId)
    {
        _adapter = adapter;
        _connection = connection;
        _steamId = steamId;
    }

    public void SendToClient(byte[] data, byte channel, DeliveryMethod method)
    {
        int sendFlag = ConvertDeliveryMethodToSteamSendFlag(method);
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
            EResult result = SteamNetworkingSockets.SendMessageToConnection(_connection, ptr, (uint)data.Length, sendFlag, out long msgNumber);
            if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
            {
                Logger.Info($"SendMessageToConnection 실패: {result} "
                    + $"(conn={_connection.m_HSteamNetConnection}, {data.Length}바이트, msgNumber={msgNumber}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void DisconnectClient(string reason)
    {
        _adapter.RequestClose(_connection, _steamId, reason);
    }

    private static int ConvertDeliveryMethodToSteamSendFlag(DeliveryMethod method) => method switch
    {
        DeliveryMethod.ReliableUnordered => 9,
        DeliveryMethod.ReliableOrdered => 9,
        DeliveryMethod.ReliableSequenced => 9,
        DeliveryMethod.Sequenced => 1,
        _ => 5,
    };
}
