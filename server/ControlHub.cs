using CasuMp.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace CasuMpServer;

public enum ClientKind
{
    Unknown,
    Agent,
    Mod,
}

public sealed class ControlHub
{
    public sealed class ClientConnection
    {
        public long Id { get; init; }
        public TcpClient Tcp { get; init; } = null!;
        public ClientKind Kind { get; set; } = ClientKind.Unknown;

        public string? MachineId { get; set; }
        public int AgentCapacity { get; set; }
        public string? AgentAddress { get; set; }
        public string? InstanceKey { get; set; }
        public int InstancePort { get; set; }
        public int InstanceDepth { get; set; }

        public bool Closed { get; set; }

        public readonly Channel<ControlMessage> Outbound = Channel.CreateUnbounded<ControlMessage>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public readonly ConcurrentDictionary<long, PendingCommand> PendingAcks = new();
    }

    public sealed class PendingCommand
    {
        public required ControlMessage Msg { get; init; }
        public DateTime Deadline { get; set; }
        public int Retries { get; set; }
        public Action<bool, string?>? OnResult { get; init; }
    }

    private static readonly TimeSpan AckRetryInterval = TimeSpan.FromSeconds(3);
    private const int AckMaxRetries = 3;

    private readonly ServerConfig _config;
    private readonly CancellationToken _ct;
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<(ClientConnection Conn, ControlMessage Msg)> _inbound = new();
    private readonly ConcurrentQueue<(ClientConnection Conn, long Seq, bool Ok, string? Reason)> _ackCompletions = new();
    private readonly ConcurrentQueue<ClientConnection> _closedConnections = new();

    private readonly List<ClientConnection> _connections = new();
    private long _seqCounter;
    private long _nextConnId;

    public ControlHub(ServerConfig config, CancellationToken ct)
    {
        _config = config;
        _ct = ct;
        _listener = new TcpListener(IPAddress.Any, config.Port);
    }

    public Func<ClientConnection, MessageType, bool>? OutboundFilter { get; set; }

    public void Start()
    {
        _listener.Start();
        Log.Info($"리스너 시작 :{_config.Port}");
        _ = AcceptLoopAsync();
    }

    public void Stop()
    {
        try { _listener.Stop(); } catch { }
        lock (_connections)
        {
            foreach (ClientConnection conn in _connections)
            {
                try { conn.Tcp.Close(); } catch { }
            }
            _connections.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                TcpClient tcp = await _listener.AcceptTcpClientAsync(_ct);
                var conn = new ClientConnection { Id = Interlocked.Increment(ref _nextConnId), Tcp = tcp };
                lock (_connections) { _connections.Add(conn); }
                _ = ServeConnectionAsync(conn);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Info($"수락 오류: {ex.Message}");
            }
        }
    }

    private async Task ServeConnectionAsync(ClientConnection conn)
    {
        using var stream = conn.Tcp.GetStream();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        Task readTask = ReadLoopAsync(conn, stream, cts.Token);
        Task writeTask = WriteLoopAsync(conn, stream, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        cts.Cancel();
        try { await Task.WhenAll(readTask, writeTask); } catch { }

        conn.Outbound.Writer.TryComplete();
        _closedConnections.Enqueue(conn);
    }

    private async Task ReadLoopAsync(ClientConnection conn, Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ControlFrame? frame = await ControlFrameCodec.ReadAsync(stream, ct);
                if (frame == null) break;

                ControlMessage? msg = ControlMessage.FromFrame(frame);
                if (msg == null) continue;

                if (msg.Type == MessageType.ACK)
                {
                    _ackCompletions.Enqueue((conn, msg.Seq, IsOk(msg), GetReason(msg)));
                }
                else
                {
                    _inbound.Enqueue((conn, msg));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Log.Info($"제어 프레임 오류 (연결 {conn.Id}): {ex.Message}");
            }
        }
    }

    private async Task WriteLoopAsync(ClientConnection conn, Stream stream, CancellationToken ct)
    {
        try
        {
            await foreach (ControlMessage msg in conn.Outbound.Reader.ReadAllAsync(ct))
            {
                await ControlFrameCodec.WriteAsync(stream, msg.ToFrame(), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public long NextSeq() => ++_seqCounter;

    public IReadOnlyList<ClientConnection> Connections
    {
        get { lock (_connections) { return _connections.ToList(); } }
    }

    public ClientConnection? AgentConnection(string machineId) => Connections.FirstOrDefault(c => c.Kind == ClientKind.Agent && c.MachineId == machineId && !c.Closed);
    public ClientConnection? ModConnection(string instanceKey) => Connections.FirstOrDefault(c => c.Kind == ClientKind.Mod && c.InstanceKey == instanceKey && !c.Closed);

    public bool Send(ClientConnection? conn, MessageType type, object? payload, Action<bool, string?>? onResult = null)
    {
        if (conn == null || conn.Closed)
        {
            onResult?.Invoke(false, "no connection");
            return false;
        }

        if (OutboundFilter != null && !OutboundFilter(conn, type))
        {
            onResult?.Invoke(false, "blocked by state");
            return false;
        }

        var msg = ControlMessage.Create(NextSeq(), type, payload);
        conn.Outbound.Writer.TryWrite(msg);
        conn.PendingAcks[msg.Seq] = new PendingCommand // ack/타임아웃 추적
        {
            Msg = msg,
            Deadline = DateTime.UtcNow + AckRetryInterval,
            Retries = 0,
            OnResult = onResult,
        };
        return true;
    }

    public void SendNoAck(ClientConnection? conn, MessageType type, object? payload)
    {
        if (conn == null || conn.Closed) return;
        if (OutboundFilter != null && !OutboundFilter(conn, type)) return;
        conn.Outbound.Writer.TryWrite(ControlMessage.Create(NextSeq(), type, payload));
    }

    public void DrainInbound(Action<ClientConnection, ControlMessage> dispatch)
    {
        while (_inbound.TryDequeue(out var item))
        {
            dispatch(item.Conn, item.Msg);
        }
    }

    public void Tick()
    {
        while (_ackCompletions.TryDequeue(out var ack))
        {
            if (ack.Conn.PendingAcks.TryRemove(ack.Seq, out PendingCommand? pending))
            {
                pending.OnResult?.Invoke(ack.Ok, ack.Reason); // ack 완료 콜백
            }
        }

        DateTime now = DateTime.UtcNow;
        foreach (ClientConnection conn in Connections)
        {
            foreach ((long seq, PendingCommand pending) in conn.PendingAcks.ToList())
            {
                if (now < pending.Deadline) continue;

                if (pending.Retries < AckMaxRetries && !conn.Closed)
                {
                    pending.Retries++;
                    pending.Deadline = now + AckRetryInterval;
                    conn.Outbound.Writer.TryWrite(pending.Msg); // 재전송
                }
                else
                {
                    conn.PendingAcks.TryRemove(seq, out _);
                    pending.OnResult?.Invoke(false, "ack timeout"); // 타임아웃
                }
            }
        }

        while (_closedConnections.TryDequeue(out ClientConnection? conn))
        {
            lock (_connections)
            {
                _connections.Remove(conn);
            }
            conn.Closed = true;
            foreach ((long _, PendingCommand pending) in conn.PendingAcks.ToList())
            {
                pending.OnResult?.Invoke(false, "connection closed");
            }
            conn.PendingAcks.Clear();
            OnConnectionClosed?.Invoke(conn); // 연결 종료 통지
        }
    }

    public event Action<ClientConnection>? OnConnectionClosed;

    private static bool IsOk(ControlMessage msg) => msg.PayloadAs<AckPayload>()?.Ok ?? false;
    private static string? GetReason(ControlMessage msg) => msg.PayloadAs<AckPayload>()?.Reason;

    private sealed class AckPayload
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
    }
}
