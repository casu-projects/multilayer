using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpGateway;

public sealed class GatewayConfig
{
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    public int DirectListenPort { get; set; } = 7790;

    public bool SteamEnabled { get; set; } = false;

    public string SteamSessionPath { get; set; } = "steam_session.json";

    public string SteamVersionTag { get; set; } = "7.0.1_MPv5.0.0";

    public string Motd { get; set; } = "";

    public double BackendRetryIntervalSeconds { get; set; } = 0.5;

    public int BackendMaxRetries { get; set; } = 600;

    public double RoutingWaitTimeoutSeconds { get; set; } = 300;

    public double CommandRetryIntervalSeconds { get; set; } = 3;

    public int CommandMaxRetries { get; set; } = 3;

    public double ControlReconnectIntervalSeconds { get; set; } = 2;

    public static GatewayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new GatewayConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true }));
            Logger.Info($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(path)) ?? new GatewayConfig();
    }
}
