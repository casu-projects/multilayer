using System.Text.Json;
using System.Text.Json.Serialization;

namespace CasuMpGateway;

/// <summary>gateway.json 스키마 (PLAN.md G12-R5). 최소 항목만 유지한다.</summary>
public sealed class GatewayConfig
{
    /// <summary>오케스트레이터 제어 채널 주소 (host:port). 게이트웨이가 연결하는 방향.
    /// 기본값은 오케스트레이터 기본 리스너(17900)와 일치 — config 자동생성 시에도 정상 연결.
    /// (에이전트의 OrchestratorAddr과 동일한 의미 — 이름 통일)</summary>
    public string OrchestratorAddr { get; set; } = "127.0.0.1:17900";

    /// <summary>직접연결(DirectIpAdapter) 리스너 포트.</summary>
    public int DirectListenPort { get; set; } = 7790;

    /// <summary>Steam 어댑터 활성화 여부 (PLAN.md G13 — Steam은 게이트웨이 1곳에만 존재).</summary>
    public bool SteamEnabled { get; set; } = false;

    /// <summary>SteamKit2 봇 세션 파일 경로 (AccountName + RefreshToken — 기존
    /// tools/SteamLoginSetup 형식과 동일). GUI/비밀번호 저장 없이 토큰 로그온.
    /// (2026-08-03 — 로비 서버명/인원/비밀번호 여부는 오케스트레이터 AUTH_INFO에서 수신 —
    /// 게이트웨이는 세션 파일 + 버전 태그만 보유)</summary>
    public string SteamSessionPath { get; set; } = "steam_session.json";

    /// <summary>로비 KeyVersion 값 (기존 오케스트레이터: GameVersionTag + "_MPv4.0.1").</summary>
    public string SteamVersionTag { get; set; } = "7.0.1_MPv4.0.1";

    // ── 타임아웃/재시도 (PLAN.md G12-R2) ──

    /// <summary>백엔드(인스턴스) 연결 재시도 간격(초).</summary>
    public double BackendRetryIntervalSeconds { get; set; } = 0.5;

    /// <summary>백엔드 연결 최대 재시도 횟수 (기본 0.5s × 600 = 5분). 콜드 인스턴스
    /// 정상 조인은 부팅 + 월드젠 동안 "is generating world" 거절을 반복하므로 넉넉히
    /// 유지한다 (READY 게이트는 마이그레이션 SWAP을 보장하므로 실부담 없음).</summary>
    public int BackendMaxRetries { get; set; } = 600;

    /// <summary>모르는 유저의 라우팅 대기 타임아웃(초) — 초과 시 "서버 준비 중"으로 거부.
    /// ROUTE-ON-READY(2026-08-02): 콜드 인스턴스 부팅+월드젠 동안 세션이 Routing 대기로
    /// 머무르므로 충분히 크게 유지한다 (기본 5분, 초과는 스폰 실패 등 비정상 상황).</summary>
    public double RoutingWaitTimeoutSeconds { get; set; } = 300;

    /// <summary>제어 명령 재전송 간격(초).</summary>
    public double CommandRetryIntervalSeconds { get; set; } = 3;

    /// <summary>제어 명령 최대 재전송 횟수.</summary>
    public int CommandMaxRetries { get; set; } = 3;

    /// <summary>제어 채널 재연결 간격(초) (PLAN.md G12-R4).</summary>
    public double ControlReconnectIntervalSeconds { get; set; } = 2;

    public static GatewayConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new GatewayConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(path)) ?? new GatewayConfig();
    }
}

/// <summary>라우팅 테이블 엔트리 — 오케스트레이터 원본의 미러 (PLAN.md G1-8).</summary>
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
