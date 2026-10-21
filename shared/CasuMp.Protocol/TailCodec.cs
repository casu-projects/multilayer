using System;
using System.Text;

namespace CasuMp.Protocol;

public static class TailCodec
{
    public const byte MagicHigh = 0xC5;
    public const byte MagicLow = 0xA5;
    public const byte VersionLegacy = 1;
    public const byte VersionWithName = 2;
    public const int FixedTailSize = 15;
    public const int MaxNameBytes = 128;

    public static byte[] Encode(ulong steamId, ushort forcedClientId, bool isReturning,
        bool isMigratingArrival, string? name)
    {
        bool withName = !string.IsNullOrEmpty(name);
        byte[] nameBytes = withName ? Encoding.UTF8.GetBytes(name!) : Array.Empty<byte>();
        if (nameBytes.Length > MaxNameBytes)
        {
            Array.Resize(ref nameBytes, MaxNameBytes);
        }
        if (nameBytes.Length == 0)
        {
            withName = false;
        }

        var buffer = new byte[FixedTailSize + (withName ? 1 + nameBytes.Length : 0)];
        int offset = 0;
        buffer[offset++] = MagicHigh;
        buffer[offset++] = MagicLow;
        buffer[offset++] = withName ? VersionWithName : VersionLegacy;
        WriteUInt64(buffer, ref offset, steamId);
        WriteUInt16(buffer, ref offset, forcedClientId);
        buffer[offset++] = isReturning ? (byte)1 : (byte)0;
        buffer[offset++] = isMigratingArrival ? (byte)1 : (byte)0;
        if (withName)
        {
            buffer[offset++] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, buffer, offset, nameBytes.Length);
        }
        return buffer;
    }

    public static bool TryFind(byte[] rawData, int userDataOffset, int userDataSize, out TailInfo info)
    {
        info = default;
        int end = userDataOffset + userDataSize;
        if (end - userDataOffset < FixedTailSize)
        {
            return false;
        }

        int searchEnd = Math.Max(userDataOffset, end - FixedTailSize - MaxNameBytes);
        for (int i = end - 2; i >= searchEnd; i--)
        {
            if (rawData[i] != MagicHigh || rawData[i + 1] != MagicLow || i + 3 >= end)
            {
                continue;
            }

            byte version = rawData[i + 2];
            if (version != VersionLegacy && version != VersionWithName)
            {
                continue;
            }
            if (i + FixedTailSize > end)
            {
                continue;
            }

            int offset = i + 3;
            ulong steamId = ReadUInt64(rawData, ref offset);
            ushort clientId = ReadUInt16(rawData, ref offset);
            bool isReturning = rawData[offset++] != 0;
            bool isMigrating = rawData[offset++] != 0;

            string? name = null;
            if (version == VersionWithName && i + FixedTailSize < end)
            {
                int nameLength = rawData[i + FixedTailSize];
                int nameStart = i + FixedTailSize + 1;
                if (nameLength > 0 && nameLength <= MaxNameBytes && nameStart + nameLength <= end)
                {
                    name = Encoding.UTF8.GetString(rawData, nameStart, nameLength);
                }
            }

            info = new TailInfo
            {
                Version = version,
                SteamId = steamId,
                ClientId = clientId,
                IsReturning = isReturning,
                IsMigratingArrival = isMigrating,
                Name = name,
            };
            return true;
        }

        return false;
    }

    private static void WriteUInt64(byte[] buffer, ref int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset++] = (byte)(value >> (8 * i));
        }
    }

    private static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
    {
        buffer[offset++] = (byte)value;
        buffer[offset++] = (byte)(value >> 8);
    }

    private static ulong ReadUInt64(byte[] buffer, ref int offset)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (ulong)buffer[offset++] << (8 * i);
        }
        return value;
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        ushort value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        offset += 2;
        return value;
    }
}

public struct TailInfo
{
    public byte Version;
    public ulong SteamId;
    public ushort ClientId;
    public bool IsReturning;
    public bool IsMigratingArrival;
    public string? Name;
}
