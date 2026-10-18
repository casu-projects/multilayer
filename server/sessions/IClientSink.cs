using LiteNetLib;

namespace CasuMpServer;

public interface IClientSink
{
    void SendToClient(byte[] data, byte channel, DeliveryMethod method);

    void DisconnectClient(string reason);
}
