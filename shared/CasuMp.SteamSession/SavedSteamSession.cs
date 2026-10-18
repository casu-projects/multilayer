namespace CasuMp.SteamSession;

public sealed class SavedSteamSession
{
    public string AccountName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string WebApi { get; set; } = "";
}
