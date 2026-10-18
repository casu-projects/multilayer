using LiteNetLib;
using LiteNetLib.Utils;

namespace CasuMpGateway;

public sealed class LiteNetLibClientSink : IClientSink
{
    private readonly NetPeer _peer;

    public LiteNetLibClientSink(NetPeer peer) => _peer = peer;

    public void SendToClient(byte[] data, byte channel, DeliveryMethod method) =>
        _peer.Send(data, channel, method);

    public void DisconnectClient(string reason)
    {
        var writer = new NetDataWriter();
        writer.Put(reason);
        _peer.Disconnect(writer);
    }
}
