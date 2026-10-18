using System.Text.Json;
using CasuMp.Runtime;

namespace CasuMp.SteamSession;

public static class SteamSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static SavedSteamSession Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SavedSteamSession>(json)
            ?? throw new InvalidOperationException("세션 파일 파싱 실패");
    }

    public static void Save(string path, SavedSteamSession session)
    {
        AtomicJsonFile.Write(path, JsonSerializer.Serialize(session, Options));
    }
}
