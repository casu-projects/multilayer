using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasuMod;

public sealed class ControlMessage
{
    private static readonly JsonSerializerSettings ParseSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public JToken Payload { get; set; }

    public string Serialize() => JsonConvert.SerializeObject(this);

    public static ControlMessage Parse(string line)
    {
        try
        {
            return JsonConvert.DeserializeObject<ControlMessage>(line, ParseSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ControlMessage Create(long seq, string type, object payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? JValue.CreateNull() : JToken.FromObject(payload),
    };

    public static ControlMessage Ack(long seq, bool ok, string reason = null) =>
        Create(seq, "ACK", new { ok, reason });

    public T PayloadAs<T>() where T : class =>
        Payload is JObject obj ? obj.ToObject<T>() : null;

    public JToken Inner(string prop) => Payload is JObject obj ? obj[prop] : null;
}

public sealed class PlayerKeyPayload
{
    public string PlayerKey { get; set; } = "";

    public int Epoch { get; set; } = -1;
}

public sealed class ChatPayload
{
    public string Speaker { get; set; } = "";

    public string Color { get; set; } = "";

    public string Layer { get; set; } = "";

    public string Prefix { get; set; } = "";

    public string PrefixColor { get; set; } = "";

    public string Message { get; set; } = "";
}

public sealed class ListResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

public sealed class ConsolePayload
{
    public string Command { get; set; } = "";
}

public sealed class RespawnPayload
{
    public string PlayerKey { get; set; } = "";
    public int FromDepth { get; set; }
}

public sealed class KickPlayerPayload
{
    public string PlayerKey { get; set; } = "";
    public string Reason { get; set; } = "";
}
