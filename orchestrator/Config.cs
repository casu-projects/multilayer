using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace CasuMpOrchestrator;

public sealed class OrchestratorConfig
{
    // 제어 허브 포트 (Gateway, Agent, Instance 연결 시 사용)
    public int Port { get; set; } = 17900;

    // 인스턴스 포트의 시작 범위
    public int InstancePortStart { get; set; } = 17790;

    // 인스턴스 포트의 최대 변위
    public int InstancePortRange { get; set; } = 100;

    public string ServerName { get; set; } = "CasuMP Server";

    public string ServerPassword { get; set; } = "";

    public int MaxPlayers { get; set; } = 32;

    public bool Verbose { get; set; } = false;

    public List<ulong> LockdownBypass { get; set; } = new();

    public double MigrationStepTimeoutSeconds { get; set; } = 30;

    // 목적지 인스턴스 READY 대기 타임아웃(초) - 콜드 인스턴스 스폰+부팅+월드젠 여유
    // READY 전에는 SWAP을 발행하지 않는다 (프리웜 + READY 게이트)
    public double MigrationReadyWaitTimeoutSeconds { get; set; } = 300;

    // 인스턴스 유휴 정지 유예(초) - 일반 인스턴스는 경과 후 종료(용량 회수),
    // Prewarm 인스턴스(레이어 1)는 경과 후 레이어 초기화(RESET - 재월드젠) 후
    // 유휴 대기한다 (프로세스 생존 - 부팅 대기 원천 제거)
    public int IdleTeardownGraceSeconds { get; set; } = 30;

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    // 제어 명령 재전송 설정
    public double CommandRetryIntervalSeconds { get; set; } = 3;
    public int CommandMaxRetries { get; set; } = 3;

    public string DiscordBotToken { get; set; } = "";

    public ulong DiscordChannelId { get; set; } = 0;

    public ulong DiscordConsoleChannelId { get; set; } = 0;

    public string SteamWebApiKey { get; set; } = "";

    public ulong DiscordAdminUserId { get; set; } = 0;

    public string DiscordUrl { get; set; } = "";

    [JsonIgnore]
    public string RunTablePath => Path.Combine(Directory.GetCurrentDirectory(), "run.json");

    [JsonIgnore]
    public string RuleTablePath => Path.Combine(Directory.GetCurrentDirectory(), "rule.json");

    [JsonIgnore]
    public string SaveRootPath => Path.Combine(Directory.GetCurrentDirectory(), "saves");

    [JsonIgnore]
    public string BanListPath => Path.Combine(Directory.GetCurrentDirectory(), "banlist.json");

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
