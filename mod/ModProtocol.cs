using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasuMod;

// 제어 프로토콜 메시지 — 오케스트레이터와 동일 규약 (JSON 라인 + seq-ack).
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

    // 마이그레이션 트랜잭션 epoch — -1이면 검증 생략 (구버전 하위 호환).
    public int Epoch { get; set; } = -1;
}

public sealed class ChatPayload
{
    public string Speaker { get; set; } = "";
    public string Message { get; set; } = "";
    public string Color { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Mode { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string[] Targets { get; set; } = Array.Empty<string>();
    public string Prefix { get; set; } = "";
    public string PrefixColor { get; set; } = "";
    // 두 번째 배지 — Discord 그룹 채팅의 "D" (파란색).
    public string Badge { get; set; } = "";
    public string BadgeColor { get; set; } = "";
}

public sealed class ListResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

public sealed class CurrentResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class DiscordResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string Url { get; set; } = "";
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

public sealed class VerbosePayload
{
    public bool On { get; set; }
}

public sealed class GroupRequestPayload
{
    public string PlayerKey { get; set; } = "";
    public string Action { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class GroupResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

public sealed class GroupInvitePayload
{
    public string PlayerKey { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string CallerName { get; set; } = "";
}

public sealed class ChatModeRequestPayload
{
    public string PlayerKey { get; set; } = "";
    public string Mode { get; set; } = "";
}

public sealed class ChatModeResultPayload
{
    public string PlayerKey { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class ChatModeResetPayload
{
    public string PlayerKey { get; set; } = "";
}

public static class KeyUtil
{
    public static string Steam(ulong steamId) => $"STEAM_{steamId}";
}
