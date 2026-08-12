using System.Text;

namespace CasuMpGateway;

// 플레이어 식별 키 - 오케스트레이터와 동일 규칙 ( Steam 경로는 실 SteamID,
// 직접연결은 username - 스푸핑 가능 수용).
public readonly record struct PlayerKey(string Value)
{
    public override string ToString() => Value;

    public static PlayerKey FromSteamId(ulong steamId) => new($"STEAM_{steamId}");

    public static PlayerKey FromUsername(string username) =>
        new($"NAME_{Convert.ToHexString(Encoding.UTF8.GetBytes(username))}");

    public static PlayerKey FromString(string value) => new(value);
}
