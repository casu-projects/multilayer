using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace CasuMpAgent;

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

    public void Ack(ControlMessage ack) => _outboundAcks.Enqueue(ack);

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
