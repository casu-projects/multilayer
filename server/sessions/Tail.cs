using LiteNetLib.Utils;

namespace CasuMpServer;

public static class TailV2
{
    public static NetDataWriter BuildConnectData(byte[] intro, ulong steamId,
        ushort forcedClientId, bool isReturning, bool isMigratingArrival, string? name = null)
    {
        var writer = new NetDataWriter();
        writer.Put(intro);
        writer.Put(TailCodec.Encode(steamId, forcedClientId, isReturning, isMigratingArrival, name));
        return writer;
    }
}
