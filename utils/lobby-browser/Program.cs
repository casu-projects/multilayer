using SteamLobbyBrowser;

class Program
{
    static async Task<int> Main(string[] args)
    {
        string sessionPath = FindSessionFile(args);
        string? versionFilter = FindOption(args, "--filter");
        if (!File.Exists(sessionPath))
        {
            Console.WriteLine($"[ERROR] Session file not found: {sessionPath}");
            Console.WriteLine("Usage: casu-lobby-browser [path/to/steam_session.json] [--filter <version>]");
            return 1;
        }

        var browser = new LobbyBrowser();
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await browser.RunAsync(sessionPath, versionFilter, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nInterrupted");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static string FindSessionFile(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length)
                return args[++i];
            if (args[i] == "--filter")
            {
                i++; // skip its value
                continue;
            }
            if (!args[i].StartsWith('-'))
                return args[i];
        }

        string cwd = Directory.GetCurrentDirectory();
        string localPath = Path.Combine(cwd, "steam_session.json");
        if (File.Exists(localPath))
            return localPath;

        string distPath = Path.Combine(cwd, "__dist__", "steam_session.json");
        if (File.Exists(distPath))
            return distPath;

        return localPath;
    }

    private static string? FindOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }
}
