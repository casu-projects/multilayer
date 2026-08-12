using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpGateway;

// 제어 프로토콜 메시지 . JSON 라인 + seq-ack
// 대소문자 무시 파싱 - 양쪽 구현체의 키 케이스 불일치로 인한 조용한 실패 방지
public sealed class ControlMessage
{
    // 직렬화 하드닝 : AllowNamedFloatingPointLiterals - NaN/Infinity
    // 포함 페이로드도 예외 없이 왕복 (연결 끊김/루프 방지 - 오케스트레이터와 동일 규약)
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

    // payload 편의 접근

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
