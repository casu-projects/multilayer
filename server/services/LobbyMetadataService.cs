using CasuMp.Protocol;

namespace CasuMpServer;

internal sealed class LobbyMetadataService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ClientGateway _gateway;
    private readonly RunRuleStore _rules;
    private readonly PlayerSessionStore _sessions;
    private readonly DiscordBot _discord;
    private readonly SteamApiClient? _steamApi;
    private readonly Dictionary<PlayerKey, bool> _alive = new();

    private DateTime _lastPushAt = DateTime.MinValue;

    public LobbyMetadataService(ClientGateway gateway, RunRuleStore rules, PlayerSessionStore sessions,
        DiscordBot discord, SteamApiClient? steamApi)
    {
        _gateway = gateway;
        _rules = rules;
        _sessions = sessions;
        _discord = discord;
        _steamApi = steamApi;
    }

    public void SetAlive(PlayerKey key, bool alive) => _alive[key] = alive;

    public void Remove(PlayerKey key) => _alive.Remove(key);

    public static ulong SteamIdOf(PlayerKey key) =>
        key.Value.StartsWith("STEAM_") && ulong.TryParse(key.Value.AsSpan(6), out ulong sid) ? sid : 0;

    public string[] BuildPlayerListLines()
    {
        var byDepth = _sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .GroupBy(s => s.Depth)
            .OrderBy(g => g.Key)
            .ToList();
        if (byDepth.Count == 0) return new[] { "No players online." };

        return byDepth.Select(g =>
            $"[L{g.Key}] {string.Join(", ", g.Select(s => s.Username ?? s.Key.Value))}").ToArray();
    }

    public void RefreshTopic()
    {
        _ = _discord.UpdatePlayerCountTopicAsync(BuildPlayerCountText());
    }

    public void Push(bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastPushAt < Interval) return;
        _lastPushAt = now;

        var online = _sessions.All.Where(s => s.Session != PlayerSessionState.Offline).ToList();
        ulong[] steamIds = online
            .Select(s => SteamIdOf(s.Key))
            .Where(id => id != 0)
            .ToArray();
        int livingCount = online.Count(s => _alive.GetValueOrDefault(s.Key, true));

        string rulesBase64 = Convert.ToBase64String(RulesBlobBuilder.Build(_rules.RuleSnapshot));
        _steamApi?.Post(new LobbyMetadataPayload
        {
            ServerName = _gateway.ServerName,
            VersionTag = _gateway.Config.SteamVersionTag,
            Motd = _gateway.Config.Motd,
            MaxPlayers = _gateway.MaxPlayers,
            HasPassword = _gateway.HasPassword,
            PlayerCount = _gateway.SessionCount,
            LivingCount = livingCount,
            HappinessSum = 0,
            SteamIds = steamIds,
            RulesBase64 = rulesBase64,
        });
    }

    private string BuildPlayerCountText()
    {
        var online = _sessions.All.Where(s => s.Session != PlayerSessionState.Offline).ToList();
        var parts = new List<string> { $"{online.Count} online" };
        foreach (var group in online.GroupBy(s => s.Depth).OrderBy(g => g.Key))
        {
            parts.Add($"[L{group.Key}] {group.Count()}");
        }
        return string.Join(" / ", parts);
    }
}
