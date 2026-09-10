using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;

class Program
{
    private static bool _connected;
    private static bool _disconnected;
    private static bool _loggedOnResult;
    private static EResult _logonResult;

    static async Task<int> Main(string[] args)
    {
        string outputPath = FindOutputPath(args);

        Console.Write("Username: ");
        string? username = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(username))
        {
            Console.WriteLine("[ERROR] Username cannot be empty");
            return 1;
        }

        Console.Write("Password: ");
        string? password = ReadPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("[ERROR] Password cannot be empty");
            return 1;
        }

        Console.WriteLine("Connecting to Steam...");

        var steamClient = new SteamClient();
        var callbackManager = new CallbackManager(steamClient);
        var steamUser = steamClient.GetHandler<SteamUser>();

        callbackManager.Subscribe<SteamClient.ConnectedCallback>(cb =>
        {
            _connected = true;
        });

        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            _disconnected = true;
        });

        callbackManager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            _logonResult = cb.Result;
            _loggedOnResult = true;
        });

        steamClient.Connect();

        while (!_connected && !_disconnected)
        {
            callbackManager.RunCallbacks();
            await Task.Delay(100);
        }

        if (_disconnected)
        {
            Console.WriteLine("[ERROR] Failed to connect to Steam");
            return 1;
        }

        Console.WriteLine("Connected. Authenticating...");

        AuthPollResult pollResponse;
        try
        {
            var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = new UserConsoleAuthenticator(),
            });

            pollResponse = await authSession.PollingWaitForResultAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Authentication failed: {ex.Message}");
            steamClient.Disconnect();
            return 1;
        }

        Console.WriteLine("Authenticated. Logging in...");

        _loggedOnResult = false;
        steamUser!.LogOn(new SteamUser.LogOnDetails
        {
            Username = pollResponse.AccountName,
            AccessToken = pollResponse.RefreshToken,
            ShouldRememberPassword = true,
        });

        while (!_loggedOnResult && !_disconnected)
        {
            callbackManager.RunCallbacks();
            await Task.Delay(100);
        }

        if (!_loggedOnResult || _logonResult != EResult.OK)
        {
            Console.WriteLine($"[ERROR] Login failed: {_logonResult}");
            steamClient.Disconnect();
            return 1;
        }

        Console.WriteLine("Successfully logged in");

        steamUser.LogOff();
        steamClient.Disconnect();

        var session = new
        {
            AccountName = pollResponse.AccountName,
            RefreshToken = pollResponse.RefreshToken,
            WebApi = ""
        };

        string json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);

        Console.WriteLine($"Session saved: {outputPath}");
        return 0;
    }

    private static string ReadPassword()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
                Console.Write("*");
            }
        }
        return new string(chars.ToArray());
    }

    private static string FindOutputPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
                return args[++i];
            if (args[i] == "-o" && i + 1 < args.Length)
                return args[++i];
        }

        string cwd = Directory.GetCurrentDirectory();
        return Path.Combine(cwd, "steam_session.json");
    }
}
