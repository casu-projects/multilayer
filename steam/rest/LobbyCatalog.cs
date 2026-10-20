using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using SteamKit2;

namespace CasuMpSteam;

public sealed record LobbyPlayer(string SteamId, string Name, string? AvatarUrl = null);

public sealed record LobbyInfo(
    string Id,
    string? Name,
    string? Description,
    string? Version,
    string? Gamemode,
    string? Layer,
    string? Depth,
    string? Language,
    int Players,
    int MaxPlayers,
    string? Living,
    string? AvgMood,
    bool HasPassword,
    bool IsDedicated,
    string[] Mods,
    List<LobbyPlayer> PlayersList);

public sealed record LobbyListResponse(int TotalPlayers, List<LobbyInfo> Lobbies, string? Error = null)
{
    public int TotalLobbies => Lobbies.Count;
}

public sealed class LobbyCatalog
{
    public const uint AppId = 4576510;

    private const string KeyPrefix = "COOP_MOD_";
    private const string LegacyKeyPrefix = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_";
    private const string SuffixLobbyName = "LOBBYNAME";
    private const string SuffixDescription = "DESCRIPTION";
    private const string SuffixVersion = "VERSION";
    private const string SuffixGamemode = "GAMEMODE";
    private const string SuffixPlrCount = "PLRCOUNT";
    private const string SuffixHasPassword = "HASPASSWORD";
    private const string SuffixIsDedicated = "ISDEDICATED";
    private const string SuffixLivingCount = "LIVINGCOUNT";
    private const string SuffixCurrentLayer = "CURRENTLAYER";
    private const string SuffixDepth = "DEPTH";
    private const string SuffixLanguage = "LANGUAGE";
    private const string SuffixMods = "MODS";
    private const string SuffixAvgMood = "AVGMOOD";
    private const string SuffixExtraData = "EXTRADATA";

    private const int ExtraDataOffset = 112;
    private const int LegacyExtraDataOffset = 100;
    private const ulong MinSteamId = 76561197960265728UL;
    private const ulong MaxSteamId = 76561202255233023UL;

    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<ulong, PlayerSummary> _playerCache = new();

    private sealed record PlayerSummary(string Name, string? AvatarUrl);

    public async Task<string?> GetAvatarAsync(ulong steamId, string? webApiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(webApiKey)) return null;

        if (_playerCache.TryGetValue(steamId, out PlayerSummary? cached)
            && !string.IsNullOrEmpty(cached.AvatarUrl))
        {
            return cached.AvatarUrl;
        }

        try
        {
            string url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/"
                + $"?key={webApiKey}&steamids={steamId}";
            string response = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(response);
            JsonElement players = doc.RootElement
                .GetProperty("response")
                .GetProperty("players");
            if (players.GetArrayLength() == 0) return null;
            JsonElement player = players[0];
            string name = player.TryGetProperty("personaname", out JsonElement nameEl)
                ? nameEl.GetString() ?? steamId.ToString()
                : steamId.ToString();
            string? avatar = player.TryGetProperty("avatarfull", out JsonElement avatarEl)
                ? avatarEl.GetString()
                : null;
            _playerCache[steamId] = new PlayerSummary(name, avatar);
            return avatar;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LobbyListResponse> BuildAsync(SteamMatchmaking.GetLobbyListCallback callback,
        string? webApiKey)
    {
        if (callback.Result != EResult.OK)
        {
            return new LobbyListResponse(0, new List<LobbyInfo>(), callback.Result.ToString());
        }

        IReadOnlyList<SteamMatchmaking.Lobby> lobbies = callback.Lobbies;
        if (callback.Lobbies.Count == 0)
        {
            return new LobbyListResponse(0, new List<LobbyInfo>());
        }

        var allSteamIds = new HashSet<ulong>();
        foreach (var lobby in lobbies)
        {
            string? extraBase64 = Meta(lobby.Metadata, SuffixExtraData);
            if (!string.IsNullOrEmpty(extraBase64))
            {
                foreach (ulong id in DecodeExtraData(extraBase64).SteamIds) allSteamIds.Add(id);
            }
        }

        if (allSteamIds.Count > 0 && !string.IsNullOrEmpty(webApiKey))
        {
            await FetchPlayerSummaries(allSteamIds, webApiKey);
        }

        var infos = new List<LobbyInfo>();
        foreach (var lobby in lobbies)
        {
            string? lobbyName = Meta(lobby.Metadata, SuffixLobbyName);
            string? description = Meta(lobby.Metadata, SuffixDescription);
            string? version = Meta(lobby.Metadata, SuffixVersion);
            string? gamemode = Meta(lobby.Metadata, SuffixGamemode);
            string? hasPassword = Meta(lobby.Metadata, SuffixHasPassword);
            string? isDedicated = Meta(lobby.Metadata, SuffixIsDedicated);
            string? livingCount = Meta(lobby.Metadata, SuffixLivingCount);
            string? currentLayer = Meta(lobby.Metadata, SuffixCurrentLayer);
            string? depth = Meta(lobby.Metadata, SuffixDepth);
            string? language = Meta(lobby.Metadata, SuffixLanguage);
            string? modsKey = Meta(lobby.Metadata, SuffixMods);
            string? avgMood = Meta(lobby.Metadata, SuffixAvgMood);
            string? plrCount = Meta(lobby.Metadata, SuffixPlrCount);
            int players = int.TryParse(plrCount, out int pc) ? pc : 0;

            ExtraData? extraData = null;
            var playersList = new List<LobbyPlayer>();
            string? extraB64 = Meta(lobby.Metadata, SuffixExtraData);
            if (!string.IsNullOrEmpty(extraB64))
            {
                extraData = DecodeExtraData(extraB64);
                foreach (ulong sid in extraData.SteamIds)
                {
                    _playerCache.TryGetValue(sid, out PlayerSummary? summary);
                    string name = summary?.Name ?? sid.ToString();
                    playersList.Add(new LobbyPlayer(sid.ToString(), name, summary?.AvatarUrl));
                }
            }

            string[] mods = extraData?.Mods ?? Array.Empty<string>();
            if (mods.Length == 0 && !string.IsNullOrEmpty(modsKey))
            {
                mods = modsKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
            }

            infos.Add(new LobbyInfo(
                lobby.SteamID.ConvertToUInt64().ToString(),
                string.IsNullOrEmpty(lobbyName) ? "(No Name)" : lobbyName,
                description,
                version,
                gamemode,
                currentLayer,
                depth,
                language,
                players,
                lobby.MaxMembers,
                livingCount,
                avgMood,
                hasPassword == "1",
                isDedicated == "1",
                mods,
                playersList));
        }

        int totalPlayers = infos.Sum(l => l.Players);
        return new LobbyListResponse(totalPlayers, infos);
    }

    private static string? Meta(IReadOnlyDictionary<string, string> metadata, string suffix)
    {
        if (metadata.TryGetValue(KeyPrefix + suffix, out string? value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }
        return metadata.TryGetValue(LegacyKeyPrefix + suffix, out value) && !string.IsNullOrEmpty(value)
            ? value
            : null;
    }

    private static ExtraData DecodeExtraData(string base64)
    {
        try
        {
            byte[] data = Convert.FromBase64String(base64);
            if (data.Length < 2) return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());

            byte[] gzipData;
            if (data[0] == 0x1F && data[1] == 0x8B)
            {
                gzipData = data;
            }
            else if (data.Length >= 4 && data[2] == 0x1F && data[3] == 0x8B)
            {
                gzipData = new byte[data.Length - 2];
                Array.Copy(data, 2, gzipData, 0, gzipData.Length);
            }
            else
            {
                int gzipStart = Array.IndexOf(data, (byte)0x1F);
                if (gzipStart >= 0 && gzipStart + 1 < data.Length && data[gzipStart + 1] == 0x8B)
                {
                    gzipData = new byte[data.Length - gzipStart];
                    Array.Copy(data, gzipStart, gzipData, 0, gzipData.Length);
                }
                else
                {
                    return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());
                }
            }

            byte[] decompressed;
            using (var inStream = new MemoryStream(gzipData))
            using (var gzip = new GZipStream(inStream, CompressionMode.Decompress))
            using (var outStream = new MemoryStream())
            {
                gzip.CopyTo(outStream);
                decompressed = outStream.ToArray();
            }

            return TryDecodeExtraData(decompressed, ExtraDataOffset)
                ?? TryDecodeExtraData(decompressed, LegacyExtraDataOffset)
                ?? new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());
        }
        catch
        {
            return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());
        }
    }

    private static ExtraData? TryDecodeExtraData(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) return null;

        int steamIdCount = BitConverter.ToUInt16(data, offset);
        if (steamIdCount <= 0 || steamIdCount > 256) return null;

        offset += 2;
        if (offset + steamIdCount * 8 > data.Length) return null;

        var ids = new ulong[steamIdCount];
        for (int i = 0; i < steamIdCount; i++)
        {
            ulong id = BitConverter.ToUInt64(data, offset);
            if (id < MinSteamId || id > MaxSteamId) return null;
            ids[i] = id;
            offset += 8;
        }

        if (offset + 1 > data.Length) return new ExtraData(ids, Array.Empty<string>());
        offset++;

        var mods = new List<string>();
        if (offset + 2 > data.Length) return new ExtraData(ids, mods.ToArray());

        int modCount = BitConverter.ToUInt16(data, offset);
        offset += 2;
        if (modCount > 256) return new ExtraData(ids, mods.ToArray());

        for (int i = 0; i < modCount; i++)
        {
            if (offset + 2 > data.Length) break;

            int rawLength = BitConverter.ToUInt16(data, offset);
            offset += 2;
            if (rawLength == 0)
            {
                mods.Add(string.Empty);
                continue;
            }

            int byteLength = rawLength - 1;
            if (offset + byteLength > data.Length) break;

            mods.Add(System.Text.Encoding.UTF8.GetString(data, offset, byteLength));
            offset += byteLength;
        }

        return new ExtraData(ids, mods.ToArray());
    }

    private async Task FetchPlayerSummaries(IEnumerable<ulong> steamIds, string webApiKey)
    {
        ulong[] ids = steamIds.Where(id => !_playerCache.ContainsKey(id)).ToArray();
        if (ids.Length == 0) return;

        for (int i = 0; i < ids.Length; i += 100)
        {
            int batch = Math.Min(100, ids.Length - i);
            string joined = string.Join(",", ids.Skip(i).Take(batch));

            try
            {
                string url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/"
                    + $"?key={webApiKey}&steamids={joined}";
                string resp = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);

                foreach (var player in doc.RootElement.GetProperty("response").GetProperty("players").EnumerateArray())
                {
                    ulong sid = ulong.Parse(player.GetProperty("steamid").GetString()!);
                    string name = player.GetProperty("personaname").GetString() ?? sid.ToString();
                    string? avatar = player.TryGetProperty("avatarfull", out JsonElement avatarEl)
                        ? avatarEl.GetString()
                        : null;
                    _playerCache[sid] = new PlayerSummary(name, avatar);
                }
            }
            catch { }
        }
    }

    private sealed record ExtraData(ulong[] SteamIds, string[] Mods);
}
