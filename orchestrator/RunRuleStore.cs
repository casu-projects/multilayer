using System.Text.Json;

namespace CasuMpOrchestrator;

// run/rule 값 중앙 저장공간
// 시작 시 로드하여 보관, MOD_HELLO 수신 시 송신한 인스턴스에 값 전송
public sealed class RunRuleStore
{
    private readonly string _runPath;
    private readonly string _rulePath;
    private readonly object _lock = new();
    private Dictionary<string, string> _run = new();
    private Dictionary<string, string> _rules = new();

    public RunRuleStore(string runPath, string rulePath)
    {
        _runPath = runPath;
        _rulePath = rulePath;
        Load();
    }

    public Dictionary<string, string> RunSnapshot
    {
        get { lock (_lock) { return new Dictionary<string, string>(_run); } }
    }

    public Dictionary<string, string> RuleSnapshot
    {
        get { lock (_lock) { return new Dictionary<string, string>(_rules); } }
    }

    public void SetRun(string key, string value)
    {
        lock (_lock)
        {
            _run[key] = value;
            Save(_runPath, _run);
        }
    }

    public void SetRule(string key, string value)
    {
        lock (_lock)
        {
            _rules[key] = value;
            Save(_rulePath, _rules);
        }
    }

    public bool ContainsRun(string key)
    {
        lock (_lock) { return _run.ContainsKey(key); }
    }

    public bool ContainsRule(string key)
    {
        lock (_lock) { return _rules.ContainsKey(key); }
    }

    private void Load()
    {
        _run = LoadFile(_runPath);
        _rules = LoadFile(_rulePath);
    }

    private static Dictionary<string, string> LoadFile(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(string path, Dictionary<string, string> data)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
