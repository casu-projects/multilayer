namespace CasuMpSteam;

internal static class SteamLog
{
    public static void Info(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} steam] {message}");
}
