using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpOrchestrator;

/// <summary>orchestrator.json 스키마 (PLAN.md O6/D).</summary>
public sealed class OrchestratorConfig
{
    /// <summary>제어 허브 리스너 포트 (게이트웨이/에이전트/모드가 연결).</summary>
    public int Port { get; set; } = 17900;

    /// <summary>인스턴스 포트 시작 범위 (오케스트레이터가 전역 배정 — G-3).</summary>
    public int InstancePortStart { get; set; } = 17790;

    /// <summary>인스턴스 포트 범위 크기.</summary>
    public int InstancePortRange { get; set; } = 100;

    /// <summary>런 설정 파일 — pwd 기준 고정 이름 (config 미노출).</summary>
    [JsonIgnore]
    public string RunTablePath => Path.Combine(Directory.GetCurrentDirectory(), "run.json");

    /// <summary>규칙 설정 파일 — pwd 기준 고정 이름 (config 미노출).</summary>
    [JsonIgnore]
    public string RuleTablePath => Path.Combine(Directory.GetCurrentDirectory(), "rule.json");

    /// <summary>플레이어 세션/데이터 저장 루트 — pwd/saves (config 미노출).</summary>
    [JsonIgnore]
    public string SaveRootPath => Path.Combine(Directory.GetCurrentDirectory(), "saves");

    /// <summary>서버 이름/비밀번호 — 인스턴스 스폰 시 에이전트에 전달.</summary>
    public string ServerName { get; set; } = "CasuMP Server";
    public string ServerPassword { get; set; } = "";

    /// <summary>최대 동시 접속 플레이어 수 — 초과 시 게이트웨이가 접속 시도 단계에서 거부
    /// (AUTH_INFO로 게이트웨이에 전달, 오케스트레이터도 SESSION_CONNECTED에서 2차 검증).</summary>
    public int MaxPlayers { get; set; } = 32;

    /// <summary>마이그레이션 트랜잭션 단계별 타임아웃(초). 월드젠(~15초) + 클라이언트 재생성 여유.</summary>
    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    /// <summary>목적지 인스턴스 READY 대기 타임아웃(초) — 콜드 인스턴스 스폰+부팅+월드젠 여유.
    /// READY 전에는 SWAP을 발행하지 않는다 (프리웜 + READY 게이트).</summary>
    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    /// <summary>인스턴스 유휴 정지 유예(초).</summary>
    public int IdleTeardownGraceSeconds { get; set; } = 30;

    /// <summary>밴 목록 파일 — pwd 기준 고정 이름 (config 미노출).</summary>
    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

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
