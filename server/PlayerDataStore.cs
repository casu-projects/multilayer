using CasuMp.Protocol;
using System.Text.Json;

namespace CasuMpServer;

public sealed class PlayerDataStore
{
    private readonly string _saveDir;
    private readonly Dictionary<PlayerKey, JsonElement> _pending = new();

    public PlayerDataStore(ServerConfig config)
    {
        _saveDir = Path.Combine(config.SaveRootPath, "players");
        Directory.CreateDirectory(_saveDir);
    }

    private string PathFor(PlayerKey key) =>
        Path.Combine(_saveDir, Sanitize(key.Value), "player.json");

    public void OnSubmit(PlayerKey key, JsonElement payload, bool migration)
    {
        WriteToDisk(key, payload); // 디스크 WAL
        if (migration)
        {
            _pending[key] = payload.Clone(); // 마이그레이션 보류
        }
        else
        {
        }
    }

    public void OnRequest(PlayerKey key, ControlHub.ClientConnection modConn, ControlHub hub, bool migrating)
    {
        if (_pending.TryGetValue(key, out JsonElement pending)) // 보류분 우선
        {
            hub.SendNoAck(modConn, MessageTypes.PlayerDataResponse, new { playerKey = key.Value, payload = pending });
            if (!migrating) _pending.Remove(key);
            return;
        }

        string path = PathFor(key);
        if (File.Exists(path))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                hub.SendNoAck(modConn, MessageTypes.PlayerDataResponse, new { playerKey = key.Value, payload = doc.RootElement });
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"{key} 디스크 로드 실패: {ex.Message} — 기본 상태 제공.");
            }
        }

        hub.SendNoAck(modConn, MessageTypes.PlayerDataResponse, new { playerKey = key.Value, payload = (JsonElement?)null });
    }

    public JsonElement? GetForMigration(PlayerKey key)
    {
        if (_pending.TryGetValue(key, out JsonElement pending))
            return pending;
        string path = PathFor(key);
        if (File.Exists(path))
        {
            try { return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement; }
            catch { }
        }
        return null;
    }

    public void CommitPending(PlayerKey key) => _pending.Remove(key);

    public void DeleteSave(PlayerKey key)
    {
        _pending.Remove(key);
        string dir = Path.GetDirectoryName(PathFor(key))!;
        try
        {
            if (File.Exists(PathFor(key))) File.Delete(PathFor(key));
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Info($"{key} 세이브 폐기 실패: {ex.Message}");
        }
    }

    private void WriteToDisk(PlayerKey key, JsonElement payload)
    {
        AtomicJsonFile.Write(PathFor(key), payload.GetRawText());
    }

    private static string Sanitize(string s)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Create(s.Length, (s, invalid), (span, state) =>
        {
            for (int i = 0; i < state.s.Length; i++)
                span[i] = state.invalid.Contains(state.s[i]) ? '_' : state.s[i];
        });
    }
}
