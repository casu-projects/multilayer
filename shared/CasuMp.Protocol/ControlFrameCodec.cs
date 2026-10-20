#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CasuMp.Protocol;

public static class ControlFrameCodec
{
    private static readonly byte[] Magic = { (byte)'C', (byte)'M', (byte)'P', (byte)'F' };

    public const byte Version = 1;
    public const int HeaderSize = 19;
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ControlFrame frame, CancellationToken ct = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        byte[] payload = frame.Payload;
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidDataException($"제어 프레임 payload가 너무 큽니다: {payload.Length}");
        }

        byte[] header = new byte[HeaderSize];
        Buffer.BlockCopy(Magic, 0, header, 0, Magic.Length);
        header[4] = Version;
        WriteUInt16(header, 5, (ushort)frame.Type);
        WriteInt64(header, 7, frame.Seq);
        WriteInt32(header, 15, payload.Length);

        await stream.WriteAsync(header, 0, HeaderSize, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
        }
    }

    public static async Task<ControlFrame?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));

        byte[] header = new byte[HeaderSize];
        if (!await ReadExactAsync(stream, header, 0, HeaderSize, ct).ConfigureAwait(false))
        {
            return null;
        }

        for (int i = 0; i < Magic.Length; i++)
        {
            if (header[i] != Magic[i])
            {
                throw new InvalidDataException("제어 프레임 magic이 올바르지 않습니다.");
            }
        }

        if (header[4] != Version)
        {
            throw new InvalidDataException($"지원하지 않는 제어 프레임 버전입니다: {header[4]}");
        }

        MessageType type = (MessageType)ReadUInt16(header, 5);
        long seq = ReadInt64(header, 7);
        int length = ReadInt32(header, 15);
        if (length < 0 || length > MaxPayloadSize)
        {
            throw new InvalidDataException($"제어 프레임 payload 길이가 올바르지 않습니다: {length}");
        }

        if (length == 0)
        {
            return new ControlFrame(seq, type, Array.Empty<byte>());
        }

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, 0, length, ct).ConfigureAwait(false))
        {
            throw new EndOfStreamException("제어 프레임 payload가 중간에 끊겼습니다.");
        }

        return new ControlFrame(seq, type, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count,
        CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException("제어 프레임이 중간에 끊겼습니다.");
            }

            total += read;
        }

        return true;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        for (int i = 0; i < 4; i++)
        {
            buffer[offset + i] = (byte)(value >> (i * 8));
        }
    }

    private static int ReadInt32(byte[] buffer, int offset)
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            value |= buffer[offset + i] << (i * 8);
        }
        return value;
    }

    private static void WriteInt64(byte[] buffer, int offset, long value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (i * 8));
        }
    }

    private static long ReadInt64(byte[] buffer, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (long)buffer[offset + i] << (i * 8);
        }
        return value;
    }
}
