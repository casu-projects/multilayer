using LiteNetLib.Utils;

namespace CasuMpServer;

public static class HandshakeReader
{
    private const int MaxNameLength = 256;
    private const int MaxCompressedCredentialsBytes = 64 * 1024;
    private const int MaxDecompressedCredentialsBytes = 64 * 1024;

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
                if (!reader.TryGetBytesWithLength(out byte[] compressed)
                    || compressed.Length == 0
                    || compressed.Length > MaxCompressedCredentialsBytes)
                {
                    return false;
                }

                using var input = new MemoryStream(compressed);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                byte[] buffer = new byte[4096];
                int total = 0;
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxDecompressedCredentialsBytes)
                        return false;
                    output.Write(buffer, 0, read);
                }

                var inner = new NetDataReader(output.ToArray());
                if (!inner.TryGetBytesWithLength(out byte[] pwBytes)
                    || pwBytes.Length > MaxNameLength)
                {
                    return false;
                }
                password = System.Text.Encoding.Latin1.GetString(pwBytes);
            }
        }
        catch
        {
            username = "";
            password = "";
            return false;
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
