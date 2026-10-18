namespace CasuMpSteam;

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }

    public bool PreferCode { get; set; }
}

public sealed class LoginCodeRequest
{
    public string? Code { get; set; }
}
