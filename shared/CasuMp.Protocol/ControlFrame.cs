#nullable enable

using System;

namespace CasuMp.Protocol;

public sealed class ControlFrame
{
    public ControlFrame(long seq, MessageType type, byte[]? payload)
    {
        Seq = seq;
        Type = type;
        Payload = payload ?? Array.Empty<byte>();
    }

    public long Seq { get; }
    public MessageType Type { get; }
    public byte[] Payload { get; }
}
