using System.Text.Json;

namespace CasuMpOrchestrator;

/// <summary>orchestrator.json 스키마 (PLAN.md O6/D).</summary>
public sealed class OrchestratorConfig
{
    /// <summary>제어 허브 리스너 포트 (게이트웨이/에이전트/모드가 연결).</summary>
    public int ControlPort { get; set; } = 17900;

    /// <summary>모드/에이전트가 오케스트레이터에 접속할 때 사용할 대외 주소 (host:port).
    /// 분산 환경에서는 이 머신의 도달 가능한 IP로 설정해야 한다 — SPAWN의 CASU_ORCH_ADDR로 전달 (G-2/D3).</summary>
    public string AdvertisedAddr { get; set; } = "127.0.0.1:17900";

    /// <summary>게이트웨이 기본 연결 주소 (게이트웨이는 우리가 연결하는 게 아니라 우리에게 옴 — 기록용).</summary>
    public string GatewayAddress { get; set; } = "127.0.0.1";

    /// <summary>인스턴스 포트 시작 범위 (오케스트레이터가 전역 배정 — G-3).</summary>
    public int InstancePortStart { get; set; } = 17790;

    /// <summary>인스턴스 포트 범위 크기.</summary>
    public int InstancePortRange { get; set; } = 100;

    /// <summary>런/규칙 설정 파일 경로.</summary>
    public string RunTablePath { get; set; } = "run.json";
    public string RuleTablePath { get; set; } = "rule.json";

    /// <summary>플레이어 세션/데이터 저장 루트 (런 수명주기와 무관한 영속).</summary>
    public string SaveRootPath { get; set; } = "saves";

    /// <summary>서버 이름/비밀번호 — 인스턴스 스폰 시 에이전트에 전달.</summary>
    public string ServerName { get; set; } = "CasuMP Server";
    public string ServerPassword { get; set; } = "";

    /// <summary>마이그레이션 트랜잭션 단계별 타임아웃(초). 월드젠(~15초) + 클라이언트 재생성 여유.</summary>
    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    /// <summary>목적지 인스턴스 READY 대기 타임아웃(초) — 콜드 인스턴스 스폰+부팅+월드젠 여유.
    /// READY 전에는 SWAP을 발행하지 않는다 (프리웜 + READY 게이트).</summary>
    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    /// <summary>인스턴스 유휴 정지 유예(초).</summary>
    public int IdleTeardownGraceSeconds { get; set; } = 30;

    /// <summary>밴 목록 파일 경로.</summary>
    public string BanListPath { get; set; } = "banlist.json";

    /// <summary>제어 명령 재전송 설정 (R1).</summary>
    public double CommandRetryIntervalSeconds { get; set; } = 3;
    public int CommandMaxRetries { get; set; } = 3;

    public static OrchestratorConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new OrchestratorConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"기본 구성 생성: {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<OrchestratorConfig>(File.ReadAllText(path)) ?? new OrchestratorConfig();
    }
}
