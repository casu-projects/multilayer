using System.Text.Json;

namespace CasuMpOrchestrator;

/// <summary>플레이어 데이터 단일 소유자 (O6-5, S9) — 접속 로드/퇴장 저장 + 마이그레이션
/// 인메모리 보류분 + WAL. 페이로드는 S9-3 스키마의 불투명 JSON (오케스트레이터는 저장/전달만).
/// 모든 접근은 메인 스레드 전용.</summary>
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

    /// <summary>퇴장/동결 시 데이터 제출. 마이그레이션이면 인메모리 보류 + 디스크 WAL,
    /// 아니면 디스크 기록만.</summary>
    public void OnSubmit(PlayerKey key, JsonElement payload, bool migration)
    {
        WriteToDisk(key, payload);
        if (migration)
        {
            _pending[key] = payload.Clone();
            Console.WriteLine($"{key} 제출 (마이그레이션 — 인메모리 보류 + WAL).");
        }
        else
        {
            Console.WriteLine($"{key} 제출 (퇴장 — 디스크 기록).");
        }
    }

    /// <summary>접속 로드 요청 (모드 → 오케스트레이터). 보류분 있으면 그것, 아니면 디스크.
    /// 데이터 없으면 payload=null (모드가 기본 상태 사용 — S9-4).</summary>
    public void OnRequest(PlayerKey key, ControlHub.ClientConnection modConn, ControlHub hub)
    {
        if (_pending.TryGetValue(key, out JsonElement pending))
        {
            hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = pending });
            _pending.Remove(key);
            Console.WriteLine($"{key} 로드 — 인메모리 보류분 제공.");
            return;
        }

        string path = PathFor(key);
        if (File.Exists(path))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = doc.RootElement });
                Console.WriteLine($"{key} 로드 — 디스크 제공.");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{key} 디스크 로드 실패: {ex.Message} — 기본 상태 제공.");
            }
        }

        hub.SendNoAck(modConn, "PLAYER_DATA_RESPONSE", new { playerKey = key.Value, payload = (JsonElement?)null });
        Console.WriteLine($"{key} 데이터 없음 — 기본 상태.");
    }

    /// <summary>마이그레이션 RESUME용 보류분 (없으면 디스크).</summary>
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

    /// <summary>마이그레이션 커밋 — 보류분 해제 (디스크 사본이 정본으로 남음).</summary>
    public void CommitPending(PlayerKey key) => _pending.Remove(key);

    /// <summary>세이브 폐기 (리스폰 = 완전 신규 취급, P5). 단일 소유자 규칙: 폐기는
    /// 오케스트레이터만 수행하며, 보류분과 디스크 사본을 함께 제거한다.</summary>
    public void DeleteSave(PlayerKey key)
    {
        _pending.Remove(key);
        string dir = Path.GetDirectoryName(PathFor(key))!;
        try
        {
            if (File.Exists(PathFor(key))) File.Delete(PathFor(key));
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
            Console.WriteLine($"{key} 세이브 폐기.");
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
        // 원자적 쓰기: 임시 파일 → 이동
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
