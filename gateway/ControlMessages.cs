using System.Text.Json;

namespace CasuMpGateway;

/// <summary>제어 프로토콜 메시지 (PLAN.md G12-R1). JSON 라인 + seq-ack.
/// 대소문자 무시 파싱 — 양쪽 구현체의 키 케이스 불일치로 인한 조용한 실패 방지.</summary>
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

    public static ControlMessage Report(long seq, string type, object? payload)
    {
        return new ControlMessage
        {
            Seq = seq,
            Type = type,
            Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload),
        };
    }

    public static ControlMessage Ack(long seq, bool ok, string? reason = null)
    {
        return new ControlMessage
        {
            Seq = seq,
            Type = "ACK",
            Payload = JsonSerializer.SerializeToElement(new { ok, reason }),
        };
    }

    // ── payload 편의 접근 ──

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
