using System;
using System.Text;

namespace CasuMp.Protocol;

public readonly struct PlayerKey : IEquatable<PlayerKey>
{
    public string Value { get; }

    public PlayerKey(string value)
    {
        Value = value ?? "";
    }

    public override string ToString() => Value;

    public static PlayerKey FromSteamId(ulong steamId) => new("STEAM_" + steamId);

    public static PlayerKey FromUsername(string username) =>
        new("NAME_" + BitConverter.ToString(Encoding.UTF8.GetBytes(username ?? "")).Replace("-", ""));

    public static PlayerKey FromString(string value) => new(value);

    public bool Equals(PlayerKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PlayerKey other && Equals(other);

    public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(PlayerKey left, PlayerKey right) => left.Equals(right);

    public static bool operator !=(PlayerKey left, PlayerKey right) => !left.Equals(right);
}
