using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CasuMpOrchestrator;

public enum ClientKind
{
    Unknown,
    Gateway,
    Agent,
    Mod,
}

// 제어 허브 - TCP 리스너로 게이트웨이/노드 에이전트/모드 3종 클라이언트를 수용 .
// JSON 라인 + seq-ack . 모든 상태 접근은 메인 스레드(Tick)에서만, 네트워크 루프는
// 백그라운드 스레드에서 큐로만 소통한다.
public sealed class ControlHub
{
    public sealed class ClientConnection
    {
        public long Id { get; init; }
        public TcpClient Tcp { get; init; } = null!;
        public ClientKind Kind { get; set; } = ClientKind.Unknown;

 // HELLO로 확정되는 신원
        public int? GatewayVersion { get; set; }
        public string? MachineId { get; set; }
        public int AgentCapacity { get; set; }
        public string? AgentAddress { get; set; }
        public string? InstanceKey { get; set; }
        public int InstancePort { get; set; }
        public int InstanceDepth { get; set; }

        public bool Closed { get; set; }

        public readonly ConcurrentQueue<ControlMessage> Outbound = new();
        public readonly ConcurrentDictionary<long, PendingCommand> PendingAcks = new();
    }

    public sealed class PendingCommand
    {
        public required ControlMessage Msg { get; init; }
        public DateTime Deadline { get; set; }
        public int Retries { get; set; }
        public Action<bool, string?>? OnResult { get; init; }
    }

    private readonly OrchestratorConfig _config;
    private readonly CancellationToken _ct;
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<(ClientConnection Conn, ControlMessage Msg)> _inbound = new();
    private readonly ConcurrentQueue<(ClientConnection Conn, long Seq, bool Ok, string? Reason)> _ackCompletions = new();
    private readonly ConcurrentQueue<ClientConnection> _closedConnections = new();

    private readonly List<ClientConnection> _connections = new();
    private long _seqCounter;
    private long _nextConnId;

    public ControlHub(OrchestratorConfig config, CancellationToken ct)
    {
        _config = config;
        _ct = ct;
        _listener = new TcpListener(IPAddress.Any, config.Port);
    }

    public void Start()
    {
        _listener.Start();
        Console.WriteLine($"리스너 시작 :{_config.Port}");
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
                Console.WriteLine($"수락 오류: {ex.Message}");
            }
        }
    }

    private async Task ServeConnectionAsync(ClientConnection conn)
    {
        using var stream = conn.Tcp.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        Task readTask = ReadLoopAsync(conn, reader, cts.Token);
        Task writeTask = WriteLoopAsync(conn, writer, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        cts.Cancel();
        try { await Task.WhenAll(readTask, writeTask); } catch { }

        _closedConnections.Enqueue(conn);
    }

    private async Task ReadLoopAsync(ClientConnection conn, StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ControlMessage? msg = ControlMessage.Parse(line);
            if (msg == null)
            {
                continue;
            }

            if (msg.Type == "ACK")
            {
                _ackCompletions.Enqueue((conn, msg.Seq, IsOk(msg), GetReason(msg)));
            }
            else
            {
                _inbound.Enqueue((conn, msg));
            }
        }
    }

    private async Task WriteLoopAsync(ClientConnection conn, StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (conn.Outbound.TryDequeue(out ControlMessage? msg))
            {
                try { await writer.WriteLineAsync(msg.Serialize().AsMemory(), ct); }
                catch { break; }
            }
            else
            {
                await Task.Delay(5, ct);
            }
        }
    }

 // 메인 스레드 API

    public long NextSeq() => ++_seqCounter;

 // 제어 채널 연결 목록 (메인 스레드 전용).
    public IReadOnlyList<ClientConnection> Connections
    {
        get { lock (_connections) { return _connections.ToList(); } }
    }

    public ClientConnection? GatewayConnection => Connections.FirstOrDefault(c => c.Kind == ClientKind.Gateway && !c.Closed);
    public ClientConnection? AgentConnection(string machineId) => Connections.FirstOrDefault(c => c.Kind == ClientKind.Agent && c.MachineId == machineId && !c.Closed);
    public ClientConnection? ModConnection(string instanceKey) => Connections.FirstOrDefault(c => c.Kind == ClientKind.Mod && c.InstanceKey == instanceKey && !c.Closed);

 // 명령 전송 + ack/타임아웃 콜백 ( 3초 × 3회 재전송). 콜백은 메인 스레드에서 실행.
    public bool Send(ClientConnection? conn, string type, object? payload, Action<bool, string?>? onResult = null)
    {
        if (conn == null || conn.Closed)
        {
            onResult?.Invoke(false, "no connection");
            return false;
        }

        var msg = ControlMessage.Create(NextSeq(), type, payload);
        conn.Outbound.Enqueue(msg);
        conn.PendingAcks[msg.Seq] = new PendingCommand
        {
            Msg = msg,
            Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_config.CommandRetryIntervalSeconds),
            Retries = 0,
            OnResult = onResult,
        };
        return true;
    }

 // 보고/응답 메시지 (ack 불필요).
    public void SendNoAck(ClientConnection? conn, string type, object? payload)
    {
        if (conn == null || conn.Closed) return;
        conn.Outbound.Enqueue(ControlMessage.Create(NextSeq(), type, payload));
    }

 // 수신 메시지 디스패치 (메인 스레드 Tick에서 호출).
    public void DrainInbound(Action<ClientConnection, ControlMessage> dispatch)
    {
        while (_inbound.TryDequeue(out var item))
        {
            dispatch(item.Conn, item.Msg);
        }
    }

 // ACK 완료 처리 + 재전송/타임아웃 + 연결 종료 처리 (메인 스레드 Tick에서 호출).
    public void Tick()
    {
 // ACK 완료
        while (_ackCompletions.TryDequeue(out var ack))
        {
            if (ack.Conn.PendingAcks.TryRemove(ack.Seq, out PendingCommand? pending))
            {
                pending.OnResult?.Invoke(ack.Ok, ack.Reason);
            }
        }

 // 재전송/타임아웃
        DateTime now = DateTime.UtcNow;
        foreach (ClientConnection conn in Connections)
        {
            foreach ((long seq, PendingCommand pending) in conn.PendingAcks.ToList())
            {
                if (now < pending.Deadline) continue;

                if (pending.Retries < _config.CommandMaxRetries && !conn.Closed)
                {
                    pending.Retries++;
                    pending.Deadline = now + TimeSpan.FromSeconds(_config.CommandRetryIntervalSeconds);
                    conn.Outbound.Enqueue(pending.Msg);
                }
                else
                {
                    conn.PendingAcks.TryRemove(seq, out _);
                    pending.OnResult?.Invoke(false, "ack timeout");
                }
            }
        }

 // 연결 종료
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
            OnConnectionClosed?.Invoke(conn);
        }
    }

 // 연결 종료 통지 (에이전트/모드/게이트웨이 이탈 처리용 - 메인 스레드에서 실행).
    public event Action<ClientConnection>? OnConnectionClosed;

    private static bool IsOk(ControlMessage msg) => msg.PayloadAs<AckPayload>()?.Ok ?? false;
    private static string? GetReason(ControlMessage msg) => msg.PayloadAs<AckPayload>()?.Reason;

    private sealed class AckPayload
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
    }
}
