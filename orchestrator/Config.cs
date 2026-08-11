using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpOrchestrator;

// orchestrator.json 스키마.
public sealed class OrchestratorConfig
{
    // 제어 허브 리스너 포트 (게이트웨이/에이전트/모드가 연결).
    public int Port { get; set; } = 17900;

    // 인스턴스 포트 시작 범위 (오케스트레이터가 전역 배정).
    public int InstancePortStart { get; set; } = 17790;

    public int InstancePortRange { get; set; } = 100;

    // pwd 기준 고정 이름 (config 미노출).
    [JsonIgnore]
    public string RunTablePath => Path.Combine(Directory.GetCurrentDirectory(), "run.json");

    [JsonIgnore]
    public string RuleTablePath => Path.Combine(Directory.GetCurrentDirectory(), "rule.json");

    [JsonIgnore]
    public string SaveRootPath => Path.Combine(Directory.GetCurrentDirectory(), "saves");

    // 서버 이름/비밀번호 — 인스턴스 스폰 시 에이전트에 전달.
    public string ServerName { get; set; } = "CasuMP Server";
    public string ServerPassword { get; set; } = "";

    // 최대 동시 접속 플레이어 수 — 초과 시 게이트웨이/오케스트레이터가 거부.
    public int MaxPlayers { get; set; } = 32;

    // 디버그 로그 표시 여부 — `verbose on/off` 명령으로 런타임 토글 + json 영속 반영.
    public bool Verbose { get; set; } = false;

    // 락다운 중에도 접속 허용할 SteamID64 목록.
    public List<ulong> LockdownBypass { get; set; } = new();

    // 마이그레이션 트랜잭션 단계별 타임아웃(초).
    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    // 목적지 인스턴스 READY 대기 타임아웃(초) — READY 전에는 SWAP을 발행하지 않는다.
    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    // 인스턴스 유휴 정지 유예(초) — 일반 인스턴스는 종료, Prewarm 인스턴스는 레이어
    // 초기화(RESET) 후 유휴 대기한다.
    public int IdleTeardownGraceSeconds { get; set; } = 30;

    // 게임 레이어 수 — 수동 마이그레이션의 "다음 레이어" 계산과 목적지 검증에 사용.
    public int MaxLayers { get; set; } = 5;

    // 프리웜 레이어 목록 — 에이전트 연결 시 이 레이어들을 미리 스폰한다. Prewarm
    // 인스턴스는 유휴 시 종료되지 않고 레이어 초기화 후 대기한다.
    public int[] PrewarmLayer { get; set; } = Array.Empty<int>();

    // 그룹 시스템 상한 — 최대 그룹 수 / 그룹당 최대 멤버 수.
    public int MaxGroups { get; set; } = 32;
    public int MaxGroupMembers { get; set; } = 16;

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // pwd 기준 고정 이름 (config 미노출).
    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

    // 제어 명령 재전송 설정.
    public double CommandRetryIntervalSeconds { get; set; } = 3;
    public int CommandMaxRetries { get; set; } = 3;

    // Discord 봇 토큰/채널 — 0 또는 빈 값이면 비활성.
    public string DiscordBotToken { get; set; } = "";
    public ulong DiscordChannelId { get; set; } = 0;

    // 콘솔 로그 릴레이/원격 명령 채널 — 채팅 채널과 반드시 분리.
    public ulong DiscordConsoleChannelId { get; set; } = 0;

    // Steam Web API 키 — 플레이어 아바타 조회용 (없으면 생략).
    public string SteamWebApiKey { get; set; } = "";

    // calladmin 알림 시 멘션할 관리자 Discord 사용자 ID — 0이면 멘션 없음.
    public ulong DiscordAdminUserId { get; set; } = 0;

    // !discord 명령으로 표시할 디스코드 서버 초대 URL.
    public string DiscordUrl { get; set; } = "";

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
