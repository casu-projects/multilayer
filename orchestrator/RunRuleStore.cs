using System.Text.Json;

namespace CasuMpOrchestrator;

// 런/규칙 설정 중앙 스토어 — run.json/rule.json (운영자 편집 정본). 시작 시 로드해 보관,
// MOD_HELLO 시 인스턴스에 전송, 콘솔 변경 시 파일 재기록 + 인스턴스 PUSH.
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

    // 없는 키의 변경을 거부하기 위한 검증.
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
