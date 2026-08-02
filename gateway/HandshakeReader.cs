using LiteNetLib.Utils;

namespace CasuMpGateway;

/// <summary>클라이언트 인트로 핸드셰이크 파싱 (G1-5: username 기반 신원).
/// 형식: [GAME_FILES_HASH(8B)][version len(2B)+bytes][username len(2B)+bytes]...</summary>
public static class HandshakeReader
{
    private const int MaxNameLength = 256;

    public static bool TryParseUsername(byte[] raw, out string username)
    {
        username = "";
        try
        {
            var reader = new NetDataReader(raw);
            reader.GetULong();
            int verLen = reader.GetUShort();
            if (verLen > MaxNameLength) return false;
            SkipBytes(reader, verLen);
            int nameLen = reader.GetUShort();
            if (nameLen <= 0 || nameLen > MaxNameLength) return false;
            username = Latin1String(reader, nameLen);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Latin1String(NetDataReader r, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = r.GetByte();
        return System.Text.Encoding.Latin1.GetString(bytes);
    }

    private static void SkipBytes(NetDataReader r, int count)
    {
        for (int i = 0; i < count; i++) r.GetByte();
    }
}
