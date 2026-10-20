using System.Net.Sockets;
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

        await ControlFrameCodec.WriteAsync(stream, _hello().ToFrame(), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task readTask = ReadLoopAsync(stream, cts.Token);
        Task writeTask = WriteLoopAsync(stream, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        cts.Cancel();
        try { await Task.WhenAll(readTask, writeTask); } catch { }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ControlFrame? frame = await ControlFrameCodec.ReadAsync(stream, ct);
            if (frame == null) break;

            ControlMessage? message = ControlMessage.FromFrame(frame);
            if (message == null || message.Type == MessageType.ACK) continue;

            _onMessage(message);
        }
    }

    private async Task WriteLoopAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            await foreach (ControlMessage message in _outbound.Reader.ReadAllAsync(ct))
            {
                await ControlFrameCodec.WriteAsync(stream, message.ToFrame(), ct);
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
