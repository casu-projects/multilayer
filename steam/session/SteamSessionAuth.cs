using SteamKit2;

namespace CasuMpSteam.Session;

public static class SteamSessionAuth
{
    public static SteamUser.LogOnDetails BuildLogOnDetails(SavedSteamSession session) => new()
    {
        Username = session.AccountName,
        AccessToken = session.RefreshToken,
        ShouldRememberPassword = true,
    };
}
