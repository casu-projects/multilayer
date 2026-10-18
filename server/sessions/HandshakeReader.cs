using LiteNetLib.Utils;

namespace CasuMpServer;

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
