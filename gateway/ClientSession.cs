using System;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpGateway;

/// <summary>단일 클라이언트 세션 (PLAN.md G3 — 상태머신 + 투명 중계 + 백엔드 연결/스왑).
/// 모든 NetManager 접근은 메인 루프 스레드에서만 일어난다.</summary>
public sealed class ClientSession : INetEventListener
{
    private static long _nextSessionId;
    private const int MaxPendingReliablePackets = 256;

    private readonly IClientSink _clientSink;
    private readonly byte[] _intro;
    private readonly GatewayCore _core;

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

    /// <summary>핸드셰이크에서 파싱한 서버 비밀번호 (DirectIpAdapter와 동일 규약 — Steam 경로
    /// 검증용. 파싱 실패 시 빈 문자열 — 게임 서버가 최종 검증하므로 안전).</summary>
    public string Password { get; }
    public SessionState State { get; private set; }
    public string? InstanceId { get; private set; }
    public string BackendAddr { get; private set; } = "";
    public bool Disposed { get; private set; }

    public ushort ForcedClientId { get; private set; }
    public bool IsReturningPlayer { get; private set; }
    public bool IsMigratingArrival { get; private set; }

    /// <summary>이 세션이 백엔드에 한 번이라도 연결 성공했는지 (재라우팅 대상 판정용).</summary>
    public bool HasEverConnectedToBackend => _hasEverConnectedToBackend;

    /// <summary>라우팅 대기 시작 시각 (Routing 상태 타임아웃용).</summary>
    public DateTime? RoutingWaitStartedAt { get; set; }

    internal ClientSession(IClientSink clientSink, byte[] intro, PlayerKey player,
        string username, ulong? steamId, GatewayCore core, string transport)
    {
        _clientSink = clientSink;
        _intro = intro;
        Player = player;
        _core = core;
        // 핸드셰이크에서 username/password 파싱 — DirectIpAdapter와 동일 규약.
        // Steam 경로는 username이 비어 전달되므로 여기서 채운다 (SESSION_CONNECTED 보고/
        // Discord 알림/!list 표시용 — 게임 서버가 최종 검증하므로 안전).
        HandshakeReader.TryParseCredentials(intro, out string introUsername, out string pw);
        Username = !string.IsNullOrEmpty(username) ? username : introUsername;
        Password = pw;
        SteamId = steamId;
        Transport = transport;
        State = SessionState.Accepted;
        _backendManager = new NetManager(this) { UnconnectedMessagesEnabled = false };
        Log.Info($"세션 생성: id={SessionId}, transport={Transport}, player={Player.Value}");
    }

    /// <summary>라우팅 대기 진입 (모르는 유저 — G12-R2).</summary>
    public void EnterRoutingWait()
    {
        State = SessionState.Routing;
        RoutingWaitStartedAt = DateTime.UtcNow;
    }

    /// <summary>라우팅 배정 (테이블 미러 or 오케스트레이터 ROUTE_UPDATE).</summary>
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

    /// <summary>마이그레이션 스왑 — 클라이언트 연결은 유지, 백엔드만 교체 (G5).
    /// 스왑 중 클라이언트 패킷은 드랍한다 (G1-6).</summary>
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
        // 새 백엔드(목적지 인스턴스)는 부팅 중일 수 있다 — 최초 연결처럼 재시도가
        // 동작하도록 상태 초기화 (_hasEverConnectedToBackend=true면 재시도가 스킵되어 튕김).
        _hasEverConnectedToBackend = false;
        _connectRetryCount = 0;
        _nextRetryAtUtc = null;
        State = SessionState.Swapping;
        Log.Info($"세션 백엔드 교체: id={SessionId}, transport={Transport}, "
            + $"{previousBackend} -> {backendAddr}, instance={instanceId ?? "-"}");
        ConnectToBackend(backendAddr);
        _swapping = false;
    }

    /// <summary>클라이언트 → 백엔드 전달. 백엔드 미연결 시 버퍼 후 연결 시 플러시.</summary>
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
            // 연결이 없는 동안 발생한 이동, 조준, 사격 같은 이상한 입력을 나중에 내보내면 더 큰 문제가 생김.
            // 그러니 신뢰성 메시지만 잠깐 보관하고, 대기열도 무한정 키우지 않게 만든다.
            if (_pendingFromClient.Count >= MaxPendingReliablePackets)
            {
                _pendingFromClient.RemoveAt(0);
                Log.Info($"신뢰성 대기열 초과: id={SessionId}, 가장 오래된 패킷 1개 폐기.");
            }
            _pendingFromClient.Add((data, channel, method));
        }
    }

    /// <summary>KICK — 클라이언트 연결 종료 + 세션 정리.</summary>
    public void Kick(string reason)
    {
        _clientSink.DisconnectClient(reason);
        Dispose();
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        State = SessionState.Closed;
        Log.Info($"세션 종료: id={SessionId}, transport={Transport}, player={Player.Value}");
        _backendManager.Stop();
    }

    /// <summary>메인 루프 틱: 백엔드 수신 이벤트 폴링 + 재시도 실행.</summary>
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
        if (!GatewayCore.IsUsableBackendAddr(addr))
        {
            // 빈/무효 주소 — 연결 시도 자체를 하지 않고 재시도 대기 (예외 → 추방 방지).
            _nextRetryAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(_core.Config.BackendRetryIntervalSeconds);
            return;
        }
        try
        {
            var (host, port) = SplitAddr(addr);
            _backendManager.PollEvents();
            // 재라우팅/재시도 경쟁 방어 (2026-08-02): 폴로 인해 직전 연결이 방금 성립된
            // 상태면 재연결을 스킵한다 — 안 하면 중복 백엔드 접속으로 인스턴스의
            // "Player with this name already exists" 추방(확정 실패 → KICK)이 발생한다.
            if (State == SessionState.Active && _backendPeer != null
                && _backendPeer.ConnectionState == ConnectionState.Connected)
            {
                return;
            }
            _backendManager.Stop();   // 이전 매니저 정리 (재라우팅/재시도 누수 방지)
            var manager = new NetManager(this) { UnconnectedMessagesEnabled = false };
            manager.Start();
            NetDataWriter connectData = TailV2.BuildConnectData(
                _intro, SteamId ?? 0UL, ForcedClientId, IsReturningPlayer, IsMigratingArrival);
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

    // ── INetEventListener (백엔드) ──

    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

    public void OnPeerConnected(NetPeer peer)
    {
        _hasEverConnectedToBackend = true;
        _connectRetryCount = 0;
        Log.Info($"{Username} - 백엔드 {BackendAddr} 연결 성공 "
            + $"(session={SessionId}, transport={Transport}, instance={InstanceId ?? "-"}).");
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

        // 명시적 거부 중 "월드 생성 중"만 일시적(재시도), 나머지는 확정 실패.
        // 게임의 실제 거부 메시지: "Server is generating world, please try again." (Net.cs:243)
        bool isDefiniteRejection = disconnectInfo.Reason == DisconnectReason.ConnectionRejected;
        if (isDefiniteRejection && rejectReason.Contains("is generating world", StringComparison.OrdinalIgnoreCase))
        {
            isDefiniteRejection = false;
        }

        if (!isDefiniteRejection && !_hasEverConnectedToBackend && !Disposed
            && _connectRetryCount < _core.Config.BackendMaxRetries)
        {
            _connectRetryCount++;
            _nextRetryAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(_core.Config.BackendRetryIntervalSeconds);
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