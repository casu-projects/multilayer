using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasuMod;

/// <summary>제어 프로토콜 메시지 — 오케스트레이터와 동일 규약 (JSON 라인 + seq-ack, R1).
/// net48 호환을 위해 Newtonsoft.Json 사용 (게임 내장).</summary>
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

    /// <summary>payload의 하위 프로퍼티 (예: PLAYER_DATA_RESPONSE의 "payload").</summary>
    public JToken Inner(string prop) => Payload is JObject obj ? obj[prop] : null;
}

/// <summary>공통 payload 구조 (마이그레이션 메시지에는 epoch 포함 — P2 멱등).</summary>
public sealed class PlayerKeyPayload
{
    public string PlayerKey { get; set; } = "";

    /// <summary>마이그레이션 트랜잭션 epoch. -1이면 검증 생략 (구버전 하위 호환).</summary>
    public int Epoch { get; set; } = -1;
}

public sealed class ChatPayload
{
    public string Speaker { get; set; } = "";

    /// <summary>닉네임 색상 (HTML hex — "#RRGGBB"). 오케스트레이터가 추가하는 레이어 태그와 함께 표시.</summary>
    public string Color { get; set; } = "";

    /// <summary>발신 인스턴스 레이어 태그 (예: "L1") — 오케스트레이터가 부여.</summary>
    public string Layer { get; set; } = "";

    /// <summary>어드민 여부 (살아있는 어드민만 true) — [*ADMIN*] 태그 표시용.</summary>
    public bool IsAdmin { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>!list 결과 (오케스트레이터 → 요청자 — 라인 배열, 각 줄을 별도 개인 채팅으로 표시).</summary>
public sealed class ListResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

/// <summary>!currentrun 결과 (오케스트레이터 → 요청자 개인 회신용).</summary>
public sealed class CurrentResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ConsolePayload
{
    public string Command { get; set; } = "";
}

/// <summary>!respawn 요청 (mod → orchestrator) — fromDepth는 발신 인스턴스 실제 depth.
/// 레이어 1은 인플레이스(로컬 리셋) 처리, 그 이상은 하향 마이그레이션 트랜잭션 처리.</summary>
public sealed class RespawnPayload
{
    public string PlayerKey { get; set; } = "";
    public int FromDepth { get; set; }
}

public static class KeyUtil
{
    public static string Steam(ulong steamId) => $"STEAM_{steamId}";
}
