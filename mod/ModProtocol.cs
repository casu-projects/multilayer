using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CasuMod;

// 제어 프로토콜 메시지 - 오케스트레이터와 동일 규약 (JSON 라인 + seq-ack)
// net48 호환을 위해 Newtonsoft.Json 사용 (게임 내장)
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

    // payload의 하위 프로퍼티 (예: PLAYER_DATA_RESPONSE의 "payload")
    public JToken Inner(string prop) => Payload is JObject obj ? obj[prop] : null;
}

// 공통 payload 구조 (마이그레이션 메시지에는 epoch 포함 - 멱등)
public sealed class PlayerKeyPayload
{
    public string PlayerKey { get; set; } = "";

    // 마이그레이션 트랜잭션 epoch. -1이면 검증 생략 (구버전 하위 호환)
    public int Epoch { get; set; } = -1;
}

public sealed class ChatPayload
{
    public string Speaker { get; set; } = "";

    // 닉네임 색상 (HTML hex - "#RRGGBB"). 오케스트레이터가 추가하는 레이어 태그와 함께 표시
    public string Color { get; set; } = "";

    // 발신 인스턴스 레이어 태그 (예: "L1") - 오케스트레이터가 부여
    public string Layer { get; set; } = "";

    // 이름 앞 배지 라벨 (예: Discord 채팅의 "D") - 클라이언트 괄호 안 "[D]"로 표시
    public string Prefix { get; set; } = "";

    // 배지 색상 (HTML hex - "#RRGGBB"). Discord 채팅은 블러플 #5865F2
    public string PrefixColor { get; set; } = "";

    public string Message { get; set; } = "";
}

// !list 결과 (오케스트레이터 -> 요청자 - 라인 배열, 각 줄을 별도 개인 채팅으로 표시)
public sealed class ListResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string[] Lines { get; set; } = Array.Empty<string>();
}

// !discord 결과 (오케스트레이터 -> 요청자 개인 회신용)
public sealed class DiscordResultPayload
{
    public string PlayerKey { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class ConsolePayload
{
    public string Command { get; set; } = "";
}

// !respawn 요청 (mod -> orchestrator) - fromDepth는 발신 인스턴스 실제 depth
// 레이어 1은 인플레이스(로컬 리셋) 처리, 그 이상은 하향 마이그레이션 트랜잭션 처리
public sealed class RespawnPayload
{
    public string PlayerKey { get; set; } = "";
    public int FromDepth { get; set; }
}

// 마이그레이션 실패 추방 (orchestrator -> mod) - Abort 시 플레이어가 연결된
// 인스턴스로 전송. 전송 레벨 킥으로 강제 재접속 -> 세이브 캡처 복구 경로 보장
public sealed class KickPlayerPayload
{
    public string PlayerKey { get; set; } = "";
    public string Reason { get; set; } = "";
}

// 디버그 로그 표시 상태 (orchestrator -> mod - `verbose on/off` 명령)
public sealed class VerbosePayload
{
    public bool On { get; set; }
}

public static class KeyUtil
{
    public static string Steam(ulong steamId) => $"STEAM_{steamId}";
}
