using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpGateway;

public sealed class ControlMessage
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public long Seq { get; set; }

    public string Type { get; set; } = "";

    public JsonElement? Payload { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, ParseOptions);

    public static ControlMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(line, ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ControlMessage Create(long seq, string type, object? payload)
    {
        return new ControlMessage
        {
            Seq = seq,
            Type = type,
            Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, ParseOptions),
        };
    }

    public static ControlMessage Ack(long seq, bool ok, string? reason = null)
    {
        return new ControlMessage
        {
            Seq = seq,
            Type = "ACK",
            Payload = JsonSerializer.SerializeToElement(new { ok, reason }, ParseOptions),
        };
    }

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
