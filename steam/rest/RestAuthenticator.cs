using SteamKit2.Authentication;

namespace CasuMpSteam;

public sealed class RestAuthenticator : IAuthenticator
{
    private readonly LoginSession _session;

    public RestAuthenticator(LoginSession session) => _session = session;

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        if (!_session.AllowDeviceConfirmation) return Task.FromResult(false);
        _session.MarkWaitingForConfirmation();
        return Task.FromResult(true);
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        _session.WaitForCodeAsync("device", null, previousCodeWasIncorrect, _session.Cancellation.Token);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        _session.WaitForCodeAsync("email", email, previousCodeWasIncorrect, _session.Cancellation.Token);
}
