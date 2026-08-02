using System.Collections.Generic;

namespace CasuMod;

/// <summary>오케스트레이터로부터 수신한 런/규칙 설정 (RUN_RULES_STATE).
/// PreRunScript.StartRun 시 RunSettingsBootstrap/RuleBootstrap이 소비한다.</summary>
public static class RunRuleState
{
    private static readonly object _lock = new();
    private static Dictionary<string, string> _runSettings = new();
    private static Dictionary<string, string> _rules = new();

    public static void Apply(RunRulesPayload payload)
    {
        lock (_lock)
        {
            _runSettings = new Dictionary<string, string>(payload?.RunSettings ?? new());
            _rules = new Dictionary<string, string>(payload?.Rules ?? new());
        }
        Plugin.Log.LogInfo($"[Rule] RUN_RULES_STATE 수신 (run {_runSettings.Count}건, rule {_rules.Count}건).");

        // R3 — 실시간 반영: 오케스트레이터 `rule`/`run` 명령 푸시가 즉시 적용된다.
        // 월드 생성 전 수신(WorldGeneration.runSettings null 등)은 가드로 no-op —
        // 기존 StartRun 부트스트랩(RuleBootstrap/RunSettingsBootstrap)이 담당한다.
        int appliedRules = RuleApplier.ApplyRulesNow();
        int appliedRuns = RuleApplier.ApplyRunSettingsNow();
        if (appliedRules > 0 || appliedRuns > 0)
        {
            Plugin.Log.LogInfo($"[Rule] 실시간 재적용 — 규칙 {appliedRules}건, 런 설정 {appliedRuns}건.");
        }
    }

    public static Dictionary<string, string> RunSettingsSnapshot()
    {
        lock (_lock) { return new Dictionary<string, string>(_runSettings); }
    }

    public static Dictionary<string, string> RulesSnapshot()
    {
        lock (_lock) { return new Dictionary<string, string>(_rules); }
    }
}

public sealed class RunRulesPayload
{
    public Dictionary<string, string> RunSettings { get; set; }
    public Dictionary<string, string> Rules { get; set; }
}
