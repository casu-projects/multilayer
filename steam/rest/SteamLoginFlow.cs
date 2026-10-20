using CasuMpSteam.Session;
using SteamKit2;
using SteamKit2.Authentication;

namespace CasuMpSteam;

internal static class SteamLoginFlow
{
    public static async Task<SavedSteamSession> RunAsync(string username, string password,
        IAuthenticator authenticator, string sessionPath, CancellationToken ct)
    {
        string oldWebApi = "";
        try
        {
            SavedSteamSession? old = SteamSessionStore.Load(sessionPath);
            oldWebApi = old?.WebApi ?? "";
        }
        catch { }

        var steamClient = new SteamClient();
        using var cancellationRegistration = ct.Register(() =>
        {
            try { steamClient.Disconnect(); } catch { }
        });
        var callbackManager = new CallbackManager(steamClient);
        var steamUser = steamClient.GetHandler<SteamUser>();

        bool connected = false;
        bool disconnected = false;
        bool loggedOnResult = false;
        EResult logonResult = EResult.Invalid;

        callbackManager.Subscribe<SteamClient.ConnectedCallback>(_ => connected = true);
        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(_ => disconnected = true);
        callbackManager.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            logonResult = callback.Result;
            loggedOnResult = true;
        });

        SteamLog.Info("Steam 연결 중...");
        steamClient.Connect();

        while (!connected && !disconnected)
        {
            ct.ThrowIfCancellationRequested();
            callbackManager.RunCallbacks();
            await Task.Delay(100, ct);
        }

        if (disconnected)
        {
            throw new InvalidOperationException("Failed to connect to Steam.");
        }

        SteamLog.Info("Steam 인증 중...");
        AuthPollResult pollResponse;
        try
        {
            var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = authenticator,
            });

            pollResponse = await authSession.PollingWaitForResultAsync();
        }
        catch (Exception ex)
        {
            steamClient.Disconnect();
            throw new InvalidOperationException($"Authentication failed: {ex.Message}", ex);
        }

        ct.ThrowIfCancellationRequested();

        SteamLog.Info("Steam 로그인 중...");
        loggedOnResult = false;
        steamUser!.LogOn(SteamSessionAuth.BuildLogOnDetails(new SavedSteamSession
        {
            AccountName = pollResponse.AccountName,
            RefreshToken = pollResponse.RefreshToken,
        }));

        while (!loggedOnResult && !disconnected)
        {
            ct.ThrowIfCancellationRequested();
            callbackManager.RunCallbacks();
            await Task.Delay(100, ct);
        }

        if (!loggedOnResult || logonResult != EResult.OK)
        {
            steamClient.Disconnect();
            throw new InvalidOperationException($"Login failed: {logonResult}");
        }

        steamUser.LogOff();
        steamClient.Disconnect();

        return new SavedSteamSession
        {
            AccountName = pollResponse.AccountName,
            RefreshToken = pollResponse.RefreshToken,
            WebApi = oldWebApi,
        };
    }
}
