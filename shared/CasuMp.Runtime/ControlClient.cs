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

    private readonly Channel<ControlMessage> _control = Channel.CreateBounded<ControlMessage>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Channel<ControlMessage> _logs = Channel.CreateBounded<ControlMessage>(
        new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly SemaphoreSlim _outboundSignal = new(0, 1);

    public ControlClient(string address, double reconnectSeconds, Func<ControlMessage> hello,
        Action<ControlMessage> onMessage, Action<string>? onError = null)
    {
        _address = address;
        _reconnectDelay = TimeSpan.FromSeconds(reconnectSeconds);
        _hello = hello;
        _onMessage = onMessage;
        _onError = onError;
    }

    public void Enqueue(ControlMessage message)
    {
        bool written = message.Type == MessageType.LOG
            ? _logs.Writer.TryWrite(message)
            : _control.Writer.TryWrite(message);
        if (!written)
        {
            _onError?.Invoke($"outbound queue full — {message.Type} 폐기");
        }
        SignalOutbound();
    }

    private void SignalOutbound()
    {
        if (_outboundSignal.CurrentCount != 0) return;
        try { _outboundSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

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
            while (!ct.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(ct);

                while (_control.Reader.TryRead(out ControlMessage? message))
                {
                    await ControlFrameCodec.WriteAsync(stream, message.ToFrame(), ct);
                }
                while (_logs.Reader.TryRead(out ControlMessage? message))
                {
                    await ControlFrameCodec.WriteAsync(stream, message.ToFrame(), ct);
                }
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
