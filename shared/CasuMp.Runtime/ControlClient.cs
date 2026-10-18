using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using CasuMp.Protocol;

namespace CasuMp.Runtime;

public sealed class ControlClient
{
    private readonly string _address;
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<ControlMessage> _hello;
    private readonly Action<ControlMessage> _onMessage;
    private readonly Action<string>? _onError;

    private readonly Channel<ControlMessage> _outbound = Channel.CreateBounded<ControlMessage>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public ControlClient(string address, double reconnectSeconds, Func<ControlMessage> hello,
        Action<ControlMessage> onMessage, Action<string>? onError = null)
    {
        _address = address;
        _reconnectDelay = TimeSpan.FromSeconds(reconnectSeconds);
        _hello = hello;
        _onMessage = onMessage;
        _onError = onError;
    }

    public void Enqueue(ControlMessage message) => _outbound.Writer.TryWrite(message);

    public async Task RunAsync(CancellationToken ct)
    {
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
                _onError?.Invoke(ex.Message);
            }

            try
            {
                await Task.Delay(_reconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        var (host, port) = SplitAddr(_address);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);

        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync(_hello().Serialize().AsMemory(), ct);

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

            ControlMessage? message = ControlMessage.Parse(line);
            if (message == null || message.Type == MessageTypes.Ack) continue;

            _onMessage(message);
        }
    }

    private async Task WriteLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        try
        {
            await foreach (ControlMessage message in _outbound.Reader.ReadAllAsync(ct))
            {
                await writer.WriteLineAsync(message.Serialize().AsMemory(), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (string Host, int Port) SplitAddr(string address)
    {
        int index = address.LastIndexOf(':');
        return (address[..index], int.Parse(address[(index + 1)..]));
    }
}
