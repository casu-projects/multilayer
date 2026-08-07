using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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

    /// <summary>인스턴스 실행 파일/스크립트 경로 (기존 run_bepinex.sh 방식).</summary>
    public string GameExecutablePath { get; set; } = "";

    /// <summary>헤드리스 실행 여부 — true면 -batchmode -nographics (기본).
    /// false면 그래픽 모드 (GPU 지원 환경에서만 — 미지원 GPU에서는 셰이더/네이티브
    /// 호출 불안정으로 인스턴스 크래시 유발 실측 2026-08-08).</summary>
    public bool HeadlessMode { get; set; } = true;

    /// <summary>인스턴스 홈 디렉토리 루트 (인스턴스당 {instancesDir}/{key}/home) — 실행 위치(pwd)
    /// 기준: 게임 폴더와 분리해 에이전트가 실행되는 디렉토리 아래 {pwd}/instances에 유지한다.
    /// [JsonIgnore] — 파생 값이라 config에 노출하지 않는다.</summary>
    [JsonIgnore]
    public string InstancesDir => Path.Combine(Directory.GetCurrentDirectory(), "instances");

    /// <summary>STOP 시 graceful 대기(초) 후 강제 종료 (G-4).</summary>
    public int StopGraceSeconds { get; set; } = 5;

    /// <summary>오케스트레이터 재연결 간격(초).</summary>
    public double ReconnectIntervalSeconds { get; set; } = 2;

    /// <summary>게이트웨이가 인스턴스에 직결할 때 사용할 호스트 IP 자동 탐지 —
    /// 첫 비루프백 IPv4 (루프백/가상 인터페이스/APIPA 제외). 게이트웨이와 같은 머신이면
    /// 루프백과 동일하게 동작하고, 게이트웨이가 원격이어도 이 머신의 LAN IP로 도달한다.
    /// 탐지 실패(인터페이스 없음) 시 127.0.0.1 폴백.</summary>
    public static string DetectLocalIPv4()
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            string name = ni.Name;
            if (name == "lo" || name.StartsWith("docker") || name.StartsWith("veth")
                || name.StartsWith("br-") || name.StartsWith("tun") || name.StartsWith("virbr")) continue;
            foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip.Address)) continue;
                byte[] b = ip.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;   // APIPA
                return ip.Address.ToString();
            }
        }
        return "127.0.0.1";
    }

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
