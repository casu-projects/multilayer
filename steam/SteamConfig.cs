using CasuMp.SteamSession;
using System.Text.Json;

namespace CasuMpSteam;

public sealed class SteamConfig
{
    public string RestListen { get; set; } = "http://127.0.0.1:17910";

    public string CoreBridgeAddr { get; set; } = "127.0.0.1:7791";

    public string BridgeSecret { get; set; } = "";

    public string SessionPath { get; set; } = "steam_session.json";

    public string WebApiKey { get; set; } = "";

    public static SteamConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            SteamConfig config = ImportLegacy(path);
            AtomicJsonFile.Write(path, JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true }));
            SteamLog.Info($"기본 구성 생성: {path}");
            return config;
        }

        SteamConfig loaded = JsonSerializer.Deserialize<SteamConfig>(File.ReadAllText(path)) ?? new SteamConfig();
        if (string.IsNullOrEmpty(loaded.WebApiKey))
        {
            loaded.WebApiKey = TryReadSessionWebApi(loaded.SessionPath);
        }
        return loaded;
    }

    private static SteamConfig ImportLegacy(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        string gatewayPath = Path.Combine(dir, "gateway.json");

        var config = new SteamConfig();
        if (File.Exists(gatewayPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(gatewayPath));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("SteamSessionPath", out JsonElement session) && session.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(session.GetString()))
                    config.SessionPath = session.GetString()!;
                SteamLog.Info($"게이트웨이 구성 가져옴: {gatewayPath}");
            }
            catch (Exception ex)
            {
                SteamLog.Info($"게이트웨이 구성 읽기 실패({gatewayPath}): {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(config.WebApiKey))
        {
            config.WebApiKey = TryReadSessionWebApi(config.SessionPath);
        }
        return config;
    }

    private static string TryReadSessionWebApi(string sessionPath)
    {
        try
        {
            SavedSteamSession? session = SteamSessionStore.Load(sessionPath);
            return session?.WebApi ?? "";
        }
        catch
        {
            return "";
        }
    }
}
