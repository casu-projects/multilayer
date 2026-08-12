using LiteNetLib;

namespace CasuMpGateway;

// 어댑터가 코어에 전달하는 클라이언트 전송 추상화 (공통 계약)
// 코어는 전송 종류(Steam P2P / LiteNetLib)를 모른다
public interface IClientSink
{
    void SendToClient(byte[] data, byte channel, DeliveryMethod method);

    void DisconnectClient(string reason);
}
