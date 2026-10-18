using System.Text;

namespace CasuMpGateway;

public readonly record struct PlayerKey(string Value)
{
    public override string ToString() => Value;

    public static PlayerKey FromSteamId(ulong steamId) => new($"STEAM_{steamId}");

    public static PlayerKey FromUsername(string username) =>
        new($"NAME_{Convert.ToHexString(Encoding.UTF8.GetBytes(username))}");

    public static PlayerKey FromString(string value) => new(value);
}
