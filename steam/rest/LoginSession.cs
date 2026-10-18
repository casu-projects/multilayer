namespace CasuMpSteam;

public sealed class LoginSession
{
    private readonly object _lock = new();
    private TaskCompletionSource<string>? _codeTcs;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string State { get; private set; } = "pending";

    public string? CodeType { get; private set; }

    public string? Email { get; private set; }

    public string? Error { get; private set; }

    public string? AccountName { get; private set; }

    public bool PreviousCodeIncorrect { get; private set; }

    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public bool AllowDeviceConfirmation { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    public LoginSession(bool allowDeviceConfirmation = true) =>
        AllowDeviceConfirmation = allowDeviceConfirmation;

    public void MarkStarting()
    {
        lock (_lock) { if (State == "pending") State = "starting"; }
    }

    public void MarkAuthenticating()
    {
        lock (_lock) { State = "authenticating"; }
    }

    public void MarkWaitingForConfirmation()
    {
        TaskCompletionSource<string>? tcs;
        lock (_lock)
        {
            if (State is "success" or "failed" or "cancelled") return;
            State = "waiting_confirmation";
            CodeType = null;
            Email = null;
            PreviousCodeIncorrect = false;
            tcs = _codeTcs;
            _codeTcs = null;
        }
        tcs?.TrySetCanceled();
    }

    public void MarkSaving()
    {
        lock (_lock) { if (State != "cancelled") State = "saving"; }
    }

    public void MarkSuccess(string? accountName)
    {
        lock (_lock)
        {
            State = "success";
            AccountName = accountName;
            Error = null;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_lock)
        {
            if (State == "cancelled") return;
            State = "failed";
            Error = error;
        }
    }

    public void Cancel()
    {
        TaskCompletionSource<string>? tcs;
        lock (_lock)
        {
            if (State is "success" or "failed" or "cancelled") return;
            State = "cancelled";
            Error = "cancelled";
            tcs = _codeTcs;
            _codeTcs = null;
        }
        tcs?.TrySetCanceled();
        try { Cancellation.Cancel(); } catch { }
    }

    public bool SubmitCode(string code)
    {
        TaskCompletionSource<string>? tcs;
        lock (_lock)
        {
            tcs = _codeTcs;
            _codeTcs = null;
            if (tcs != null) State = "authenticating";
        }
        return tcs != null && tcs.TrySetResult(code);
    }

    internal Task<string> WaitForCodeAsync(string codeType, string? email, bool previousCodeWasIncorrect,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _codeTcs?.TrySetCanceled();
            _codeTcs = tcs;
            State = "waiting_code";
            CodeType = codeType;
            Email = email;
            PreviousCodeIncorrect = previousCodeWasIncorrect;
        }
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                id = Id,
                state = State,
                codeType = CodeType,
                email = Email,
                error = Error,
                accountName = AccountName,
                previousCodeIncorrect = PreviousCodeIncorrect,
                allowDeviceConfirmation = AllowDeviceConfirmation,
                startedAtUtc = StartedAtUtc,
            };
        }
    }
}
