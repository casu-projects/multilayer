using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpAgent;

public sealed class AgentConfig
{
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    public string MachineId { get; set; } = "casu-agent-1";

    public int Capacity { get; set; } = 5;

    public string GameExecutablePath { get; set; } = "";

    public bool HeadlessMode { get; set; } = true;

    public int StopGraceSeconds { get; set; } = 5;

    public double ReconnectIntervalSeconds { get; set; } = 2;

    public string WebAddr { get; set; } = "http://127.0.0.1:17920";

    public string WebUploadToken { get; set; } = "";

    [JsonIgnore]
    public string InstancesDir => Path.Combine(Directory.GetCurrentDirectory(), "instances");

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AgentConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            Logger.Info($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path)) ?? new AgentConfig();
    }
}
