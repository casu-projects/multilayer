using CasuMp.Protocol;
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
        Create(seq, MessageTypes.Ack, new { ok, reason });

    public T PayloadAs<T>() where T : class =>
        Payload is JObject obj ? obj.ToObject<T>() : null;

    public JToken Inner(string prop) => Payload is JObject obj ? obj[prop] : null;
}
