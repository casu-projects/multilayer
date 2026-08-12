using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpOrchestrator;

// 플레이어 식별 키 - 게이트웨이와 동일 규칙
public readonly record struct PlayerKey(string Value)
{
    public override string ToString() => Value;
    public static PlayerKey FromSteamId(ulong steamId) => new($"STEAM_{steamId}");
    public static PlayerKey FromUsername(string username) =>
        new($"NAME_{Convert.ToHexString(Encoding.UTF8.GetBytes(username))}");
    public static PlayerKey FromString(string value) => new(value);
}

// 제어 프로토콜 메시지 - 게이트웨이 -R1과 동일 규약 (JSON 라인 + seq-ack)
public sealed class ControlMessage
{
    // 직렬화 하드닝 : AllowNamedFloatingPointLiterals - 플레이어 데이터에
    // NaN/Infinity가 포함되어도 (손상된 세이브 등) 직렬화/파싱이 예외로 죽지 않고
    // "NaN"/"Infinity" 리터럴로 왕복된다 - 잘못된 값 하나가 제어 채널 연결을 끊어
    // 인스턴스 연결 루프/크래시 처리 홍수로 번지는 것을 방지
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
