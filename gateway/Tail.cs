using LiteNetLib.Utils;

namespace CasuMpGateway;

// 백엔드 신원 tail(고정 15B): [Magic 0xC5A5][Ver 1|2][SteamID64][forcedClientId][isReturning][isMigratingArrival].
// Ver 2는 뒤에 [nameLen][UTF-8 이름]을 덧붙여 CJK 이름을 보정한다.
// mod의 OnConnectionRequest_TailV2Patch가 매직/버전을 검증하고 불일치 시 접속을 거부한다.
public static class TailV2
{
    public const byte MagicHigh = 0xC5;
    public const byte MagicLow = 0xA5;
    public const byte VersionLegacy = 1;   // 이름 없음
    public const byte VersionWithName = 2; // [nameLen 1B][UTF-8 이름] 추가
    public const int TailSize = 2 + 1 + 8 + 2 + 1 + 1;
    public const int MaxNameBytes = 128;

    public static NetDataWriter BuildConnectData(byte[] intro, ulong steamId,
        ushort forcedClientId, bool isReturning, bool isMigratingArrival, string? name = null)
    {
        bool withName = !string.IsNullOrEmpty(name);
        var writer = new NetDataWriter();
        writer.Put(intro);
        writer.Put(MagicHigh);
        writer.Put(MagicLow);
        writer.Put(withName ? VersionWithName : VersionLegacy);
        writer.Put(steamId);
        writer.Put(forcedClientId);
        writer.Put(isReturning);
        writer.Put(isMigratingArrival);
        if (withName)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name!);
            if (nameBytes.Length > MaxNameBytes)
            {
                nameBytes = nameBytes[..MaxNameBytes];
            }
            writer.Put((byte)nameBytes.Length);
            writer.Put(nameBytes);
        }
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
