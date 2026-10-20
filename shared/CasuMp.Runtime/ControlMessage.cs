using System.Text.Json;
using System.Text.Json.Serialization;
using CasuMp.Protocol;

namespace CasuMp.Runtime;

public sealed class ControlMessage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public long Seq { get; set; }
    public MessageType Type { get; set; } = MessageType.UNKNOWN;
    public JsonElement? Payload { get; set; }

    public ControlFrame ToFrame()
    {
        byte[] payload = Payload is JsonElement element
            ? JsonSerializer.SerializeToUtf8Bytes(element, JsonOptions)
            : Array.Empty<byte>();
        return new ControlFrame(Seq, Type, payload);
    }

    public static ControlMessage? FromFrame(ControlFrame frame)
    {
        try
        {
            JsonElement? payload = frame.Payload.Length == 0
                ? null
                : JsonSerializer.Deserialize<JsonElement>(frame.Payload, JsonOptions);
            return new ControlMessage { Seq = frame.Seq, Type = frame.Type, Payload = payload };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ControlMessage Create(long seq, MessageType type, object? payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions),
    };

    public static ControlMessage Ack(long seq, bool ok, string? reason = null) =>
        Create(seq, MessageType.ACK, new { ok, reason });

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement element ? element.Deserialize<T>(JsonOptions) : null;
}
