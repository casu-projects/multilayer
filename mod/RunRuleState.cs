using CasuMp.Protocol;
using System.Collections.Generic;

namespace CasuMod;

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
