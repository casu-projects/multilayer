using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace CasuMpAgent;

// 제어 채널 (오케스트레이터 ↔ 에이전트) - TCP JSON 라인
// 에이전트가 오케스트레이터에 연결하는 방향. 재연결 시 보류 보고/로그가 flush된다
// 명령은 read 루프에서 수신 -> (msg, ack) 쌍으로 inbound 큐, ACK/보고/로그는 write 루프가 전송
public sealed class ControlChannel
{
    private readonly AgentConfig _config;
    private readonly ConcurrentQueue<(ControlMessage Msg, Action<ControlMessage> Ack)> _inbound;
    private readonly ConcurrentQueue<ControlMessage> _outboundAcks = new();
    private readonly ConcurrentQueue<ControlMessage> _outboundReports = new();
    private readonly string _localIp;

    public ControlChannel(AgentConfig config, string localIp,
        ConcurrentQueue<(ControlMessage, Action<ControlMessage>)> inbound)
    {
        _config = config;
        _localIp = localIp;
        _inbound = inbound;
    }

    // ACK 전송 (read 루프가 명령과 함께 넘긴 콜백에서 호출)
    public void Ack(ControlMessage ack) => _outboundAcks.Enqueue(ack);

    // 보고 메시지 전송 (연결 수립 시 flush - 재연결 후 재전송)
    public void Report(ControlMessage msg) => _outboundReports.Enqueue(msg);

    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan delay = TimeSpan.FromSeconds(_config.ReconnectIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 연결 오류는 릴레이 없이 자체 stdout에만 - 재시도 루프가 오케스트레이터
                // 콘솔을 도배하지 않도록 한다 (정상 연결 로그는 릴레이됨)
                Logger.Local($"오케스트레이터 연결 오류: {ex.Message}");
            }
            await Task.Delay(delay, ct);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        var (host, port) = SplitAddr(_config.OrchestratorAddr);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        Logger.Info($"오케스트레이터 연결: {_config.OrchestratorAddr}");

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        writer.WriteLine(ControlMessage.Create(1, "AGENT_HELLO", new
        {
            machineId = _config.MachineId,
            capacity = _config.Capacity,
            address = _localIp,
        }).Serialize());

        // 보류 보고 flush
        while (_outboundReports.TryDequeue(out ControlMessage? report))
        {
            await writer.WriteLineAsync(report.Serialize().AsMemory(), ct);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task readTask = ReadLoopAsync(reader, cts.Token);
        Task writeTask = WriteLoopAsync(writer, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        cts.Cancel();
        try { await Task.WhenAll(readTask, writeTask); } catch { }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ControlMessage? msg = ControlMessage.Parse(line);
            if (msg == null || msg.Type == "ACK") continue; // 우리가 보낸 보고의 ACK - 무시

            _inbound.Enqueue((msg, Ack));
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ControlMessage? msg = null;
            if (_outboundAcks.TryDequeue(out ControlMessage? ack)) msg = ack;
            else if (_outboundReports.TryDequeue(out ControlMessage? report)) msg = report;
            else if (Logger.TryDequeue(out ControlMessage? log)) msg = log;
            if (msg != null)
            {
                await writer.WriteLineAsync(msg.Serialize().AsMemory(), ct);
            }
            else
            {
                await Task.Delay(5, ct);
            }
        }
    }

    private static (string Host, int Port) SplitAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        return (addr[..idx], int.Parse(addr[(idx + 1)..]));
    }
}
