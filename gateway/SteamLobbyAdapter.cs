using System.Runtime.InteropServices;
using LiteNetLib;
using Steamworks;

namespace CasuMpGateway;

/// <summary>SteamLobbyAdapter — Steam P2P 수신 + 로비 (G2/G13).
/// 클라이언트는 "로비 소유자"에게 P2P 접속(바닐라 고정)하므로, Steamworks.NET 사용자 세션으로
/// P2P를 종단하고 SteamID64를 네이티브로 확보한 뒤 ClientSession을 코어에 넘긴다.
/// 로비(SteamKit2)는 SteamLobby가 담당. 인스턴스는 여전히 순수 LiteNetLib (tail v2로 신원 전달).</summary>
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
        _lobby = new SteamLobby(config.SteamSessionPath, config.SteamVersionTag, core);
    }

    public void Start()
    {
        try
        {
            ESteamAPIInitResult initResult = SteamAPI.InitEx(out string initErrMsg);
            if (initResult != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Log.Info($"SteamAPI.InitEx() 실패: {initResult} — {initErrMsg} "
                    + "Steam P2P는 비활성화됩니다.");
                return;
            }

            _initialized = true;
            Log.Info("SteamAPI.InitEx() 성공.");

            SteamNetworkingUtils.InitRelayNetworkAccess();
            _connectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, Array.Empty<SteamNetworkingConfigValue_t>());
        }
        catch (DllNotFoundException ex)
        {
            Log.Info($"Steam P2P 비활성화: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Info($"Steam P2P 비활성화: {ex.Message}");
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

        if (DateTime.UtcNow >= _nextQualityLogAtUtc)
        {
            _nextQualityLogAtUtc = DateTime.UtcNow.AddSeconds(10);
            LogConnectionQuality();
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

    /// <summary>오케스트레이터 LOBBY_METADATA → 로비 메타데이터 반영.</summary>
    public void UpdateLobbyMetadata(GatewayCore.LobbyMetadataPayload payload) => _lobby.UpdateDynamicMetadata(payload);

    /// <summary>주기적 P2P 연결 품질 로그 (10초 간격) — direct/relay 여부는 연결 성립 시
    /// 상세 상태(GetDetailedConnectionStatus — "transport" 라인)로, 실시간 품질은
    /// GetConnectionRealTimeStatus(핑/품질/패킷률/백로그)로 관찰한다.
    /// 릴레이 경유 유저 식별 + 손실/백로그 실측 — 동기화 문제의 데이터 기반 판단용.</summary>
    private DateTime _nextQualityLogAtUtc = DateTime.MinValue;

    private void LogConnectionQuality()
    {
        foreach (HSteamNetConnection conn in _activeConnections.ToList())
        {
            try
            {
                SteamNetConnectionRealTimeStatus_t rt = default;
                SteamNetConnectionRealTimeLaneStatus_t lane = default;
                if (SteamNetworkingSockets.GetConnectionRealTimeStatus(
                        conn, ref rt, 1, ref lane)
                    != EResult.k_EResultOK)
                {
                    continue;
                }
                ulong sid = _connectionSteamIds.TryGetValue(conn, out ulong s) ? s : 0;
                Log.Debug($"P2P 품질 steam={sid} ping={rt.m_nPing}ms "
                    + $"qual(L/R)={(int)Math.Round(rt.m_flConnectionQualityLocal * 100.0)}%"
                    + $"/{(int)Math.Round(rt.m_flConnectionQualityRemote * 100.0)}% "
                    + $"pps(out/in)={rt.m_flOutPacketsPerSec:F0}/{rt.m_flInPacketsPerSec:F0} "
                    + $"pendRel={rt.m_cbPendingReliable} sentUnackedRel={rt.m_cbSentUnackedReliable} "
                    + $"pendUnrel={rt.m_cbPendingUnreliable}");
            }
            catch (Exception ex)
            {
                Log.Debug($"P2P 품질 조회 실패: {ex.Message}");
            }
        }
    }

    private static void LogDetailedConnectionStatus(HSteamNetConnection conn, ulong steamId)
    {
        try
        {
            int len = SteamNetworkingSockets.GetDetailedConnectionStatus(conn, out string detail, 2048);
            if (len > 0 && !string.IsNullOrEmpty(detail))
            {
                Log.Debug($"P2P 상세 steam={steamId}:\n{detail}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"P2P 상세 조회 실패: {ex.Message}");
        }
    }

    /// <summary>Steam 세션 KICK: 로비 채팅 사유 전달 후 지연 종료 (채팅이 CM 왕복을 먼저 마치게).</summary>
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
                    Log.Debug($"P2P 연결 수락: {callback.m_info.m_identityRemote.GetSteamID64()}");
                }
                else
                {
                    Log.Info($"P2P 연결 수락 실패: {result}");
                }
                break;
            }
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
            {
                ulong sid = callback.m_info.m_identityRemote.GetSteamID64();
                Log.Debug($"P2P 연결 성립: {sid}");
                LogDetailedConnectionStatus(callback.m_hConn, sid);
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
            if (message.m_cbSize <= 1)
            {
                return;
            }

            // Steam P2P wire: [payload][1-byte send flag] (bit 0x8 = ReliableOrdered).
            int payloadLength = message.m_cbSize - 1;
            var payload = new byte[payloadLength];
            Marshal.Copy(message.m_pData, payload, 0, payloadLength);
            byte sendFlagByte = Marshal.ReadByte(message.m_pData, payloadLength);
            DeliveryMethod method = (sendFlagByte & 0x8) != 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;

            if (_sessionsByConnection.TryGetValue(conn, out ClientSession? session))
            {
                session.ForwardFromClient(payload, channel: 0, method);
            }
            else
            {
                // 첫 메시지 = 인트로 → 세션 생성 → 코어 라우팅.
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
                    // 라우팅 직전 거부 (밴/유지보수 등) — 로비 채팅 사유는 RequestClose가 전달.
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

/// <summary>Steam P2P 클라이언트 싱크 — wire 형식([payload][1바이트 flag]) 인코딩.
/// DisconnectClient(reason)는 어댑터의 지연 종료 경로(로비 채팅 사유 전달)를 사용한다.</summary>
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

        var framed = new byte[data.Length + 1];
        Array.Copy(data, framed, data.Length);
        framed[data.Length] = (byte)sendFlag;

        IntPtr ptr = Marshal.AllocHGlobal(framed.Length);
        try
        {
            Marshal.Copy(framed, 0, ptr, framed.Length);
            EResult result = SteamNetworkingSockets.SendMessageToConnection(_connection, ptr, (uint)framed.Length, sendFlag, out long msgNumber);
            if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
            {
                Log.Info($"SendMessageToConnection 실패: {result} "
                    + $"(conn={_connection.m_HSteamNetConnection}, {framed.Length}바이트, msgNumber={msgNumber}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void DisconnectClient(string reason)
    {
        // 원시 P2P 종료 사유는 Steam 클라이언트에 표시되지 않으므로, 로비 채팅 KICK으로 사유를
        // 전달한 뒤 1.5초 후 실제 종료 (채팅이 CM 왕복을 마칠 시간 확보).
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