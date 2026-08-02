using System.Net.Sockets;
using System.Text;

namespace CasuMpGateway;

/// <summary>제어 채널 (오케스트레이터 ↔ 게이트웨이) — TCP JSON 라인 (G12-R1).
/// 게이트웨이가 오케스트레이터에 연결하는 방향. 재연결 시 테이블 재동기화 + 활성 세션 재보고 (R4).
/// 명령은 read 루프에서 수신 → 코어 큐, ACK/보고는 write 루프가 전송.</summary>
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
                Log.Info($"연결 오류: {ex.Message}");
            }
            await Task.Delay(reconnectDelay, ct);
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        var (host, port) = SplitAddr(_config.ControlEndpoint);
        await client.ConnectAsync(host, port, ct);
        Log.Info($"오케스트레이터 연결 성공: {_config.ControlEndpoint}");

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        // R4: 연결 직후 HELLO + 활성 세션 재보고 → 오케스트레이터가 TABLE_SNAPSHOT 전송.
        writer.WriteLine(ControlMessage.Report(_core.NextSeq(), "GATEWAY_HELLO",
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
            if (msg == null)
            {
                Log.Info($"파싱 불가 라인 (길이 {line.Length}) — 무시.");
                continue;
            }
            _core.EnqueueCommand(msg);
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ControlMessage? msg = _core.TryDequeueOutbound();
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
