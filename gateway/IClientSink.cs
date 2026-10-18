using LiteNetLib;

namespace CasuMpGateway;

public interface IClientSink
{
    void SendToClient(byte[] data, byte channel, DeliveryMethod method);

    void DisconnectClient(string reason);
}
