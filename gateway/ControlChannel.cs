using System.Net.Sockets;
using System.Text;

namespace CasuMpGateway;

public sealed class ControlChannel
{
    private readonly GatewayConfig _config;
    private readonly GatewayCore _core;

    public ControlChannel(GatewayConfig config, GatewayCore core)
    {
        _config = config;
        _core = core;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan reconnectDelay = TimeSpan.FromSeconds(_config.ControlReconnectIntervalSeconds);
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
                Logger.Local($"연결 오류: {ex.Message}");
            }
            await Task.Delay(reconnectDelay, ct);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        var (host, port) = SplitAddr(_config.OrchestratorAddr);
        await client.ConnectAsync(host, port, ct);
        Logger.Info($"오케스트레이터 연결 성공: {_config.OrchestratorAddr}");

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        writer.WriteLine(ControlMessage.Create(_core.NextSeq(), "GATEWAY_HELLO",
            new { version = 1 }).Serialize());
        _core.ReportActiveSessions();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task readTask = ReadLoopAsync(reader, cts.Token);
        Task writeTask = WriteLoopAsync(writer, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        cts.Cancel();
        await Task.WhenAll(readTask, writeTask);
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break; // 상대방 종료

            if (string.IsNullOrWhiteSpace(line)) continue;
            ControlMessage? msg = ControlMessage.Parse(line);
            if (msg == null || msg.Type == "ACK") continue;
            _core.EnqueueCommand(msg);
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ControlMessage? msg = _core.TryDequeueOutbound();
            if (msg == null && Logger.TryDequeue(out ControlMessage? log)) msg = log;
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
