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

    /// <summary>인스턴스 유휴 정지 유예(초) — 일반 인스턴스는 경과 후 종료(용량 회수),
    /// Prewarm 인스턴스(PrewarmLayer 소속)는 경과 후 레이어 초기화(RESET — 재월드젠) 후
    /// 유휴 대기한다 (프로세스 생존 — 부팅 대기 원천 제거).</summary>
    public int IdleTeardownGraceSeconds { get; set; } = 30;

    /// <summary>게임 레이어 수 — 수동 마이그레이션(`migrate`)의 "다음 레이어" 계산과
    /// 목적지 유효 범위 검증에 사용 (모드가 LAYER_END로 보고하는 값과 일치해야 한다).</summary>
    public int MaxLayers { get; set; } = 5;

    /// <summary>프리웜 레이어 목록 — 에이전트 연결 시 이 레이어들을 수용량 내 최대한
    /// 미리 스폰한다 (기본 [] — 수요 기반 스폰만). Prewarm 인스턴스는 유휴 시 종료되지
    /// 않고 레이어 초기화 후 유휴 대기하므로, 용량은 |PrewarmLayer| + 동시 발생 가능한
    /// 수요 레이어 수를 커버해야 한다 (2026-08-03).</summary>
    public int[] PrewarmLayer { get; set; } = Array.Empty<int>();

    /// <summary>어드민 SteamID 목록 — 채팅 [*ADMIN*] 태그 표시 대상 (콘솔 `admin add/remove`
    /// 명령으로 수정·영속화 — orchestrator.json에 저장).</summary>
    public string[] AdminSteamIds { get; set; } = Array.Empty<string>();

    /// <summary>config 파일 저장 (콘솔 `admin add/remove` 영속화 — 전체 직렬화, 기존 값 보존).</summary>
    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>밴 목록 파일 — pwd 기준 고정 이름 (config 미노출).</summary>
    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

    /// <summary>제어 명령 재전송 설정 (R1).</summary>
    public double CommandRetryIntervalSeconds { get; set; } = 3;
    public int CommandMaxRetries { get; set; } = 3;

    /// <summary>Discord 봇 토큰 — 빈 값이면 봇 비활성 (D1).</summary>
    public string DiscordBotToken { get; set; } = "";

    /// <summary>Discord 채팅/알림 채널 ID — 0이면 봇 비활성 (D1).</summary>
    public ulong DiscordChannelId { get; set; } = 0;

    /// <summary>Discord 콘솔 채널 ID — 0이면 콘솔 로그 릴레이/원격 명령 비활성 (D1).
    /// 이 채널에 전체 콘솔 로그가 전송되고, 채널 메시지가 서버 콘솔 명령으로 실행된다
    /// (채팅 채널과 반드시 분리 — 동일 시 콘솔 채널 경로가 우선).</summary>
    public ulong DiscordConsoleChannelId { get; set; } = 0;

    /// <summary>Steam Web API 키 — 플레이어 아바타 조회용 (없으면 아바타 생략, D1).</summary>
    public string SteamWebApiKey { get; set; } = "";

    /// <summary>calladmin 알림 시 멘션할 관리자 Discord 사용자 ID — 0이면 멘션 없음.</summary>
    public ulong DiscordAdminUserId { get; set; } = 0;

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
