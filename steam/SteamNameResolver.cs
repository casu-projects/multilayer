using Steamworks;

namespace CasuMpSteam;

internal static class SteamNameResolver
{
    internal static bool IsBrokenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        foreach (char c in name)
        {
            if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }

    internal static string? ResolveName(ulong steamId)
    {
        try
        {
            if (!SteamAPI.IsSteamRunning()) return null;
            string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
