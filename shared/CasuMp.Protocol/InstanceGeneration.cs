#nullable enable
using System;

namespace CasuMp.Protocol;

public static class InstanceGeneration
{
    public static bool Matches(string? expected, string? actual) =>
        string.IsNullOrEmpty(expected)
        || string.Equals(expected, actual, StringComparison.Ordinal);

    public static bool MatchesExact(string? expected, string? actual) =>
        string.Equals(expected ?? "", actual ?? "", StringComparison.Ordinal);
}
