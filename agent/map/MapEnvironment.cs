using System.Diagnostics;

namespace CasuMpAgent;

internal static class MapEnvironment
{
    public static void ApplyTo(ProcessStartInfo psi, AgentConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.WebAddr))
            psi.Environment["CASU_WEB_ADDR"] = config.WebAddr;
        if (!string.IsNullOrWhiteSpace(config.WebUploadToken))
            psi.Environment["CASU_WEB_TOKEN"] = config.WebUploadToken;
    }
}
