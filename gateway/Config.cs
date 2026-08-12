using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpGateway;

public sealed class GatewayConfig
{
    // 오케스트레이터 IP:Port
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    public int DirectListenPort { get; set; } = 7790;

    public bool SteamEnabled { get; set; } = false;

    public string SteamSessionPath { get; set; } = "steam_session.json";

    public string SteamVersionTag { get; set; } = "7.0.1_MPv4.0.1";

    // 서버 브라우저 Mods 버튼 툴팁으로 표시할 MOTD 라인 (EXTRADATA modlist로 전달)
    public string[] Motd { get; set; } = Array.Empty<string>();

    public double BackendRetryIntervalSeconds { get; set; } = 0.5;

    // 백엔드 연결 최대 재시도 (0.5s x 600 = 5분 - 콜드 인스턴스 부팅+월드젠 동안 거절 반복)
    public int BackendMaxRetries { get; set; } = 600;

    // 모르는 유저 라우팅 대기 타임아웃(초) - 콜드 인스턴스 부팅 대기라 넉넉히 유지
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

// 라우팅 테이블 엔트리 - 오케스트레이터 원본의 미러
public sealed class RouteEntry
{
    [JsonPropertyName("playerKey")]
    public string PlayerKey { get; set; } = "";

    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("clientId")]
    public ushort ClientId { get; set; }

    [JsonPropertyName("backendAddr")]
    public string BackendAddr { get; set; } = "";

    [JsonPropertyName("isReturning")]
    public bool IsReturning { get; set; }
}
