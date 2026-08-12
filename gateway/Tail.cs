using LiteNetLib.Utils;

namespace CasuMpGateway;

// 백엔드 신원 전달 - tail v2
// [클라 인트로 원본][Magic(2B) 0xC5A5][Ver(1B) 1][SteamID64(8B)][forcedClientId(2B)]
// [isReturning(1B)][isMigratingArrival(1B)] - 총 15바이트 tail
// mod 쪽 ForwardRealSteamId가 매직+버전 검증 후 파싱한다 (불일치 시 접속 거부 - fail-fast)
public static class TailV2
{
    public const byte MagicHigh = 0xC5;
    public const byte MagicLow = 0xA5;
    public const byte Version = 1;
    public const int TailSize = 2 + 1 + 8 + 2 + 1 + 1;

    public static NetDataWriter BuildConnectData(byte[] intro, ulong steamId,
        ushort forcedClientId, bool isReturning, bool isMigratingArrival)
    {
        var writer = new NetDataWriter();
        writer.Put(intro);
        writer.Put(MagicHigh);
        writer.Put(MagicLow);
        writer.Put(Version);
        writer.Put(steamId);
        writer.Put(forcedClientId);
        writer.Put(isReturning);
        writer.Put(isMigratingArrival);
        return writer;
    }

    // connect data에서 tail 시작 오프셋을 찾는다. 실패 시 -1
    public static int FindTailOffset(byte[] rawData, int userDataOffset, int userDataSize)
    {
        int end = userDataOffset + userDataSize;
        if (end - userDataOffset < TailSize)
            return -1;

        int searchStart = userDataOffset + (end - userDataOffset - TailSize);
        for (int i = searchStart; i >= userDataOffset; i--)
        {
            if (i + 1 < end && rawData[i] == MagicHigh && rawData[i + 1] == MagicLow)
                return i;
        }
        return -1;
    }
}
