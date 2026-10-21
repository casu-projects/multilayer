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

    public string Description { get; set; } = "";

    public List<ulong> LockdownBypass { get; set; } = new();

    public int InstanceReadyTimeoutSeconds { get; set; } = 300;

    public int IdleShutdownDelaySeconds { get; set; } = 30;

    public int DirectListenPort { get; set; } = 7790;

    public bool SteamEnabled { get; set; } = false;

    public int SteamBridgePort { get; set; } = 7791;

    public string SteamBridgeSecret { get; set; } = "";

    public string SteamApiUrl { get; set; } = "http://127.0.0.1:17910";

    public string SteamApiToken { get; set; } = "";

    public bool WebEnabled { get; set; } = true;

    public string WebListen { get; set; } = "http://0.0.0.0:17920";

    public string DiscordBotToken { get; set; } = "";

    public ulong DiscordChannelId { get; set; } = 0;

    public ulong DiscordConsoleChannelId { get; set; } = 0;

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
            var config = new ServerConfig();
            AtomicJsonFile.Write(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"기본 구성 생성: {path}");
            return config;
        }
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }
}
