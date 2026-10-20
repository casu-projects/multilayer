using CasuMp.Protocol;
using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasuMod;

public sealed class ControlMessage
{
    public long Seq { get; set; }
    public MessageType Type { get; set; } = MessageType.UNKNOWN;
    public JToken Payload { get; set; }

    public ControlFrame ToFrame()
    {
        byte[] payload = Payload == null || Payload.Type == JTokenType.Null
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(Payload.ToString(Formatting.None));
        return new ControlFrame(Seq, Type, payload);
    }

    public static ControlMessage FromFrame(ControlFrame frame)
    {
        try
        {
            JToken payload = frame.Payload.Length == 0
                ? JValue.CreateNull()
                : JToken.Parse(Encoding.UTF8.GetString(frame.Payload));
            return new ControlMessage { Seq = frame.Seq, Type = frame.Type, Payload = payload };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ControlMessage Create(long seq, MessageType type, object payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? JValue.CreateNull() : JToken.FromObject(payload),
    };

    public static ControlMessage Ack(long seq, bool ok, string reason = null) =>
        Create(seq, MessageType.ACK, new { ok, reason });

    public T PayloadAs<T>() where T : class =>
        Payload is JObject obj ? obj.ToObject<T>() : null;

    public JToken Inner(string prop) => Payload is JObject obj ? obj[prop] : null;
}
