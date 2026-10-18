using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpOrchestrator;

public sealed class OrchestratorConfig
{
    public int Port { get; set; } = 17900;

    public int InstancePortStart { get; set; } = 17790;

    public int InstancePortRange { get; set; } = 100;

    public string ServerName { get; set; } = "CasuMP Server";

    public string ServerPassword { get; set; } = "";

    public int MaxPlayers { get; set; } = 32;

    public bool PrewarmIdleShutdown { get; set; } = true;

    public List<ulong> LockdownBypass { get; set; } = new();

    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    public int IdleTeardownGraceSeconds { get; set; } = 30;

    public void Save(string path)
    {
        AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public double CommandRetryIntervalSeconds { get; set; } = 3;
    public int CommandMaxRetries { get; set; } = 3;

    public string DiscordBotToken { get; set; } = "";

    public ulong DiscordChannelId { get; set; } = 0;

    public ulong DiscordConsoleChannelId { get; set; } = 0;

    public string SteamWebApiKey { get; set; } = "";

    public ulong DiscordAdminUserId { get; set; } = 0;

    [JsonIgnore]
    public string RunTablePath => Path.Combine(Directory.GetCurrentDirectory(), "run.json");

    [JsonIgnore]
    public string RuleTablePath => Path.Combine(Directory.GetCurrentDirectory(), "rule.json");

    [JsonIgnore]
    public string SaveRootPath => Path.Combine(Directory.GetCurrentDirectory(), "saves");

    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

    public static OrchestratorConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new OrchestratorConfig();
            AtomicJsonFile.Write(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<OrchestratorConfig>(File.ReadAllText(path)) ?? new OrchestratorConfig();
    }
}
