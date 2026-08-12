using LiteNetLib.Utils;

namespace CasuMpGateway;

// 클라이언트 인트로 핸드셰이크 파싱 (username 기반 신원)
// 형식: [GAME_FILES_HASH(8B)][version len(2B)+bytes][username len(2B)+bytes]...
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

    // 클라이언트 인트로 파싱 + 압축 섹션에서 비밀번호 추출 (게임
    // ReadClientConnectIntroductionPacket 형식 미러링. 형식은 클래스 문서 참조)
    // 압축 섹션 파싱 실패 시 password="" - 게임 서버가 최종 검증하므로 추출 가능할 때만
    // 조기 거부에 사용한다 (게임 버전 변경에 안전하게 폴백)
    public static bool TryParseCredentials(byte[] raw, out string username, out string password)
    {
        username = "";
        password = "";
        if (!TryParseUsername(raw, out username)) return false;

        try
        {
            var reader = new NetDataReader(raw);
            reader.GetULong();
            int verLen = reader.GetUShort();
            if (verLen > MaxNameLength) return false;
            SkipBytes(reader, verLen);
            int nameLen = reader.GetUShort();
            if (nameLen <= 0 || nameLen > MaxNameLength) return false;
            SkipBytes(reader, nameLen);

            if (!reader.EndOfData)
            {
                // 압축 섹션: [len(2B)+gzip][password(GetBytesWithLength - 이 빌드 2B)][color][secret][modlist]
                // 게임 확장 Get(out string, oneByteChars:true) 미러링: GetBytesWithLength + char-byte 변환
                byte[] compressed = reader.GetBytesWithLength();
                using var input = new MemoryStream(compressed);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                var inner = new NetDataReader(output.ToArray());
                byte[] pwBytes = inner.GetBytesWithLength();
                password = System.Text.Encoding.Latin1.GetString(pwBytes);
            }
        }
        catch
        {
            password = "";
        }
        return true;
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
