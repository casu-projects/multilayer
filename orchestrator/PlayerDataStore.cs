using System.Text.Json;

namespace CasuMpOrchestrator;

// 플레이어 데이터 단일 소유자 - 접속 로드/퇴장 저장 + 마이그레이션
// 인메모리 보류분 + WAL. 페이로드는 스키마의 불투명 JSON (오케스트레이터는 저장/전달만)
// 모든 접근은 메인 스레드 전용
public sealed class PlayerDataStore
{
    private readonly string _saveDir;
    private readonly Dictionary<PlayerKey, JsonElement> _pending = new();

    public PlayerDataStore(OrchestratorConfig config)
    {
        _saveDir = Path.Combine(config.SaveRootPath, "players");
        Directory.CreateDirectory(_saveDir);
    }

    private string PathFor(PlayerKey key) =>
        Path.Combine(_saveDir, Sanitize(key.Value), "player.json");

    // 퇴장/동결 시 데이터 제출. 마이그레이션이면 인메모리 보류 + 디스크 WAL,
    // 아니면 디스크 기록만
    public void OnSubmit(PlayerKey key, JsonElement payload, bool migration)
    {
        WriteToDisk(key, payload);
        if (migration)
        {
            _pending[key] = payload.Clone();
        }
        else
        {
        }
    }

    // 접속 로드 요청 (모드 -> 오케스트레이터). 보류분 있으면 그것, 아니면 디스크
    // 데이터 없으면 payload=null (모드가 기본 상태 사용 - )
    // 마이그레이션 진행 중이면 보류분을 소비·제거하지 않고 유지한다 - 요청이 보류분을
    // 제거하면 이후 RESUME(GetForMigration)이 디스크로 폴백해 방금 제출된 최신 상태가
    // 아닌 스테일 데이터를 전달할 수 있다. 보류분은 마이그레이션 커밋(CommitPending)이
    // 제거하므로 잔여가 발생하지 않는다
    public void OnRequest(PlayerKey key, ControlHub.ClientConnection modConn, ControlHub hub, bool migrating)
    {
        if (_pending.TryGetValue(key, out JsonElement pending))
        {
            hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = pending });
            if (!migrating) _pending.Remove(key);
            return;
        }

        string path = PathFor(key);
        if (File.Exists(path))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = doc.RootElement });
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{key} 디스크 로드 실패: {ex.Message} — 기본 상태 제공.");
            }
        }

        hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = (JsonElement?)null });
    }

    // 마이그레이션 RESUME용 보류분 (없으면 디스크)
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

    // 마이그레이션 커밋 - 보류분 해제 (디스크 사본이 정본으로 남음)
    public void CommitPending(PlayerKey key) => _pending.Remove(key);

    // 세이브 폐기 (리스폰 = 완전 신규 취급). 단일 소유자 규칙: 폐기는
    // 오케스트레이터만 수행하며, 보류분과 디스크 사본을 함께 제거한다
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
            Console.WriteLine($"{key} 세이브 폐기 실패: {ex.Message}");
        }
    }

    private void WriteToDisk(PlayerKey key, JsonElement payload)
    {
        string dir = Path.GetDirectoryName(PathFor(key))!;
        Directory.CreateDirectory(dir);
        // 원자적 쓰기: 임시 파일 -> 이동
        string tmp = PathFor(key) + ".tmp";
        File.WriteAllText(tmp, payload.GetRawText());
        File.Move(tmp, PathFor(key), overwrite: true);
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
