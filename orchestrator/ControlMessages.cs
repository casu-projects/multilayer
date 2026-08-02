using System.Text;
using System.Text.Json;

namespace CasuMpOrchestrator;

/// <summary>플레이어 식별 키 — 게이트웨이와 동일 규칙.</summary>
public readonly record struct PlayerKey(string Value)
{
    public override string ToString() => Value;
    public static PlayerKey FromSteamId(ulong steamId) => new($"STEAM_{steamId}");
    public static PlayerKey FromUsername(string username) =>
        new($"NAME_{Convert.ToHexString(Encoding.UTF8.GetBytes(username))}");
    public static PlayerKey FromString(string value) => new(value);
}

/// <summary>제어 프로토콜 메시지 — 게이트웨이 G12-R1과 동일 규약 (JSON 라인 + seq-ack).</summary>
public sealed class ControlMessage
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

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

    public static ControlMessage Create(long seq, string type, object? payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload),
    };

    public static ControlMessage Ack(long seq, bool ok, string? reason = null) =>
        Create(seq, "ACK", new { ok, reason });

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
