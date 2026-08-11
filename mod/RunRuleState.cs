using System.Collections.Generic;

namespace CasuMod;

// 오케스트레이터가 전달한 런/규칙 설정 (RUN_RULES_STATE) 보관소.
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

        RuleApplier.ApplyRulesNow();
        RuleApplier.ApplyRunSettingsNow();
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
