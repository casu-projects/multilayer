using System.Text.Json;

namespace CasuMpAgent;

/// <summary>agent.json 스키마 (D3/G-3/G-4).</summary>
public sealed class AgentConfig
{
    /// <summary>오케스트레이터 제어 허브 주소 (host:port).</summary>
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    /// <summary>머신 ID — 오케스트레이터의 배치/식별 키.</summary>
    public string MachineId { get; set; } = "m1";

    /// <summary>동시 실행 가능한 인스턴스 수 (배치 결정용).</summary>
    public int Capacity { get; set; } = 4;

    /// <summary>이 머신의 대외 주소 — 게이트웨이가 인스턴스에 연결할 때 사용 (D4).</summary>
    public string Address { get; set; } = "127.0.0.1";

    /// <summary>인스턴스 실행 파일/스크립트 경로 (기존 run_bepinex.sh 방식).</summary>
    public string GameExecutablePath { get; set; } = "";

    /// <summary>게임 실행 인자 템플릿 (구 오케스트레이터 GameArgsTemplate과 동일).</summary>
    public string GameArgsTemplate { get; set; } =
        "{GAMEEXEPATH} --ksmulti-servername \"{SERVERNAME}\" --ksmulti-setpass \"{PASSWORD}\" "
        + "--ksmulti-runcommand \"startserver {PORT}\" "
        + "-batchmode -nographics -logFile \"{UNITYLOGPATH}\"";

    /// <summary>인스턴스 홈 디렉토리 루트 (인스턴스당 {instancesDir}/{key}/home).</summary>
    public string InstancesDir { get; set; } = "./instances";

    /// <summary>STOP 시 graceful 대기(초) 후 강제 종료 (G-4).</summary>
    public int StopGraceSeconds { get; set; } = 5;

    /// <summary>오케스트레이터 재연결 간격(초).</summary>
    public double ReconnectIntervalSeconds { get; set; } = 2;

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AgentConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            AgentLog.Info($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path)) ?? new AgentConfig();
    }
}

/// <summary>제어 프로토콜 메시지 — 오케스트레이터와 동일 규약 (JSON 라인 + seq-ack).</summary>
public sealed class ControlMessage
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static ControlMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(line, ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ControlMessage Create(long seq, string type, object? payload) => new()
    {
        Seq = seq,
        Type = type,
        Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload),
    };

    public T? PayloadAs<T>() where T : class =>
        Payload is JsonElement el ? el.Deserialize<T>(ParseOptions) : null;
}
