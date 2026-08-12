using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpAgent;

public sealed class AgentConfig
{
    // 오케스트레이터 IP:Port
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    public string MachineId { get; set; } = "casu-agent-1";

    // 해당 에이전트가 최대 수용 가능한 인스턴스의 수량
    public int Capacity { get; set; } = 4;

    public string GameExecutablePath { get; set; } = "";

    // 헤드리스 모드 여부 (-batchmode -nographics)
    public bool HeadlessMode { get; set; } = true;

    // STOP 시 graceful 대기 후 강제 종료 시간(초)
    public int StopGraceSeconds { get; set; } = 5;

    public double ReconnectIntervalSeconds { get; set; } = 2;

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
