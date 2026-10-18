using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpServer;

public sealed class ServerConfig
{
    public int Port { get; set; } = 17900;

    public int InstancePortStart { get; set; } = 17790;

    public int InstancePortRange { get; set; } = 100;

    public string ServerName { get; set; } = "CasuMP Server";

    public string ServerPassword { get; set; } = "";

    public int MaxPlayers { get; set; } = 32;

    public string SteamVersionTag { get; set; } = "7.0.1_MPv5.0.0";

    public string Motd { get; set; } = "";

    public bool PrewarmIdleShutdown { get; set; } = true;

    public List<ulong> LockdownBypass { get; set; } = new();

    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    public int IdleTeardownGraceSeconds { get; set; } = 30;

    public double CommandRetryIntervalSeconds { get; set; } = 3;

    public int CommandMaxRetries { get; set; } = 3;

    public int DirectListenPort { get; set; } = 7790;

    public bool SteamEnabled { get; set; } = false;

    public int SteamBridgePort { get; set; } = 7791;

    public string SteamBridgeSecret { get; set; } = "";

    public string SteamApiUrl { get; set; } = "http://127.0.0.1:17910";

    public bool WebEnabled { get; set; } = true;

    public string WebListen { get; set; } = "http://0.0.0.0:17920";

    public string WebUploadToken { get; set; } = "";

    public double BackendRetryIntervalSeconds { get; set; } = 0.5;

    public int BackendMaxRetries { get; set; } = 600;

    public double RoutingWaitTimeoutSeconds { get; set; } = 300;

    public string DiscordBotToken { get; set; } = "";

    public ulong DiscordChannelId { get; set; } = 0;

    public ulong DiscordConsoleChannelId { get; set; } = 0;

    public ulong DiscordAdminUserId { get; set; } = 0;

    [JsonIgnore]
    public string RunTablePath => Path.Combine(Directory.GetCurrentDirectory(), "run.json");

    [JsonIgnore]
    public string RuleTablePath => Path.Combine(Directory.GetCurrentDirectory(), "rule.json");

    [JsonIgnore]
    public string SaveRootPath => Path.Combine(Directory.GetCurrentDirectory(), "saves");

    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

    public void Save(string path)
    {
        AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            ServerConfig config = ImportLegacy(path);
            AtomicJsonFile.Write(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"기본 구성 생성: {path}");
            return config;
        }
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }

    private static ServerConfig ImportLegacy(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        string orchestratorPath = Path.Combine(dir, "orchestrator.json");
        string gatewayPath = Path.Combine(dir, "gateway.json");

        var config = new ServerConfig();
        if (File.Exists(orchestratorPath))
        {
            try
            {
                var old = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(orchestratorPath));
                if (old != null)
                {
                    config = old;
                    Log.Info($"기존 구성 가져옴: {orchestratorPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"기존 구성 읽기 실패({orchestratorPath}): {ex.Message}");
            }
        }

        if (File.Exists(gatewayPath))
        {
            try
            {
                var gateway = JsonSerializer.Deserialize<GatewayImport>(File.ReadAllText(gatewayPath));
                if (gateway != null)
                {
                    config.DirectListenPort = gateway.DirectListenPort;
                    config.SteamEnabled = gateway.SteamEnabled;
                    if (!string.IsNullOrEmpty(gateway.SteamVersionTag)) config.SteamVersionTag = gateway.SteamVersionTag;
                    if (gateway.Motd != null) config.Motd = gateway.Motd;
                    config.BackendRetryIntervalSeconds = gateway.BackendRetryIntervalSeconds;
                    config.BackendMaxRetries = gateway.BackendMaxRetries;
                    config.RoutingWaitTimeoutSeconds = gateway.RoutingWaitTimeoutSeconds;
                    Log.Info($"게이트웨이 구성 가져옴: {gatewayPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"게이트웨이 구성 읽기 실패({gatewayPath}): {ex.Message}");
            }
        }

        return config;
    }

    private sealed class GatewayImport
    {
        public int DirectListenPort { get; set; } = 7790;
        public bool SteamEnabled { get; set; }
        public string SteamVersionTag { get; set; } = "7.0.1_MPv5.0.0";
        public string Motd { get; set; } = "";
        public double BackendRetryIntervalSeconds { get; set; } = 0.5;
        public int BackendMaxRetries { get; set; } = 600;
        public double RoutingWaitTimeoutSeconds { get; set; } = 300;
    }
}
