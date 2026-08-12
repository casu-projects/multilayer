using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpAgent;

// 제어 프로토콜 메시지 - 오케스트레이터와 동일 규약 (JSON 라인 + seq-ack)
public sealed class ControlMessage
{
    // 직렬화 하드닝 : AllowNamedFloatingPointLiterals - NaN/Infinity 포함 페이로드도
    // 예외 없이 "NaN"/"Infinity" 리터럴로 왕복 (잘못된 값 하나가 채널 연결을 끊는 것 방지)
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

    public static ControlMessage Create(long seq, string type, object? payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, ParseOptions),
    };

    public static ControlMessage Ack(long seq, bool ok, string? reason = null) =>
        Create(seq, "ACK", new { ok, reason });

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
