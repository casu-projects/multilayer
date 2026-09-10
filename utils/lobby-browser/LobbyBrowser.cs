using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using SteamKit2;

namespace SteamLobbyBrowser;

internal sealed class LobbyBrowser
{
    private const uint AppId = 4576510;

    private const string MetaLobbyName = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LOBBYNAME";
    private const string MetaVersion = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_VERSION";
    private const string MetaGamemode = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_GAMEMODE";
    private const string MetaPlrCount = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_PLRCOUNT";
    private const string MetaHasPassword = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_HASPASSWORD";
    private const string MetaIsDedicated = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_ISDEDICATED";
    private const string MetaLivingCount = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_LIVINGCOUNT";
    private const string MetaCurrentLayer = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_CURRENTLAYER";
    private const string MetaAvgMood = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_AVGMOOD";
    private const string MetaExtraData = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_EXTRADATA";

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamMatchmaking? _matchmaking;
    private readonly HttpClient _http = new();

    private bool _loggedOn;
    private bool _lobbyListReceived;
    private SavedSteamSession? _session;
    private Dictionary<ulong, string> _nameCache = new();

    public LobbyBrowser() { }

    public async Task RunAsync(string sessionPath, CancellationToken ct)
    {
        _session = LoadSession(sessionPath);

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>();
        _matchmaking = _steamClient.GetHandler<SteamMatchmaking>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamMatchmaking.GetLobbyListCallback>(OnLobbyListReceived);

        _steamClient.Connect();

        while (!ct.IsCancellationRequested)
        {
            _callbackManager.RunCallbacks();
            await Task.Delay(100, ct);

            if (_lobbyListReceived)
            {
                break;
            }
        }

        _steamUser?.LogOff();
        _steamClient.Disconnect();

        await Task.Delay(500, ct);
    }

    private SavedSteamSession LoadSession(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SavedSteamSession>(json)
            ?? throw new InvalidOperationException("세션 파일 파싱 실패");
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _steamUser!.LogOn(new SteamUser.LogOnDetails
        {
            Username = _session!.AccountName,
            AccessToken = _session.RefreshToken,
            ShouldRememberPassword = true,
        });
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            Console.WriteLine($"[ERROR] Login failed: {callback.Result}");
            return;
        }

        _loggedOn = true;
        Console.WriteLine("Successfully logged in");
        Console.WriteLine("Searching Lobbies...");

        var filters = new List<SteamMatchmaking.Lobby.Filter>
        {
            new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide)
        };
        _matchmaking!.GetLobbyList(AppId, filters, maxLobbies: 50);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _loggedOn = false;
    }

    private async void OnLobbyListReceived(SteamMatchmaking.GetLobbyListCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            Console.WriteLine($"[오류] 로비 검색 실패: {callback.Result}");
            _lobbyListReceived = true;
            return;
        }

        if (callback.Lobbies.Count == 0)
        {
            Console.WriteLine("No public lobbies found.");
            _lobbyListReceived = true;
            return;
        }

        int totalPlayers = callback.Lobbies.Sum(l =>
        {
            l.Metadata.TryGetValue(MetaPlrCount, out string? pc);
            return int.TryParse(pc, out int n) ? n : 0;
        });
        Console.WriteLine($"\nTotal {callback.Lobbies.Count} lobbies, {totalPlayers} players\n");

        var allSteamIds = new HashSet<ulong>();
        foreach (var lobby in callback.Lobbies)
        {
            if (lobby.Metadata.TryGetValue(MetaExtraData, out string? extraBase64))
            {
                foreach (ulong id in DecodeExtraData(extraBase64).SteamIds)
                    allSteamIds.Add(id);
            }
        }

        if (allSteamIds.Count > 0 && !string.IsNullOrEmpty(_session?.WebApi))
        {
            await FetchPlayerNames(allSteamIds);
        }

        int index = 1;
        foreach (var lobby in callback.Lobbies)
        {
            lobby.Metadata.TryGetValue(MetaLobbyName, out string? lobbyName);
            lobby.Metadata.TryGetValue(MetaVersion, out string? version);
            lobby.Metadata.TryGetValue(MetaGamemode, out string? gamemode);
            lobby.Metadata.TryGetValue(MetaHasPassword, out string? hasPassword);
            lobby.Metadata.TryGetValue(MetaIsDedicated, out string? isDedicated);
            lobby.Metadata.TryGetValue(MetaLivingCount, out string? livingCount);
            lobby.Metadata.TryGetValue(MetaCurrentLayer, out string? currentLayer);
            lobby.Metadata.TryGetValue(MetaAvgMood, out string? avgMood);

            string pwIcon = hasPassword == "1" ? " \uf023" : "";
            string dedIcon = isDedicated == "1" ? " \uf108" : "";
            string lobbyLabel = string.IsNullOrEmpty(lobbyName) ? "(No Name)" : lobbyName;

            lobby.Metadata.TryGetValue(MetaPlrCount, out string? plrCount);
            string plrDisplay = int.TryParse(plrCount, out int pc) ? pc.ToString() : "0";

            Console.WriteLine($"#{index} {lobbyLabel}{pwIcon}{dedIcon} <{lobby.SteamID.ConvertToUInt64()}>");
            if (!string.IsNullOrEmpty(version)) Console.WriteLine($"    Version:   {version}");
            if (!string.IsNullOrEmpty(gamemode)) Console.WriteLine($"    Mode:      {gamemode}");
            if (!string.IsNullOrEmpty(currentLayer)) Console.WriteLine($"    Layer:     {currentLayer}");
            Console.WriteLine($"    Players:   {plrDisplay}/{lobby.MaxMembers}");

            ExtraData? extraData = null;
            if (lobby.Metadata.TryGetValue(MetaExtraData, out string? extraBase64) && !string.IsNullOrEmpty(extraBase64))
            {
                extraData = DecodeExtraData(extraBase64);
                foreach (ulong sid in extraData.SteamIds)
                {
                    string name = _nameCache.TryGetValue(sid, out string? n) ? n : sid.ToString();
                    Console.WriteLine($"    \u2514 {name} <{sid}>");
                }
            }

            if (!string.IsNullOrEmpty(livingCount)) Console.WriteLine($"    Survived:  {livingCount}/{plrDisplay}");
            if (!string.IsNullOrEmpty(avgMood)) Console.WriteLine($"    AvgMood:   {avgMood}");
            if (extraData?.Mods.Length > 0)
            {
                Console.WriteLine($"    Mods:      {extraData.Mods.Length}");
                foreach (string mod in extraData.Mods)
                    Console.WriteLine($"    \u2514 {mod}");
            }

            Console.WriteLine();
            index++;
        }

        _lobbyListReceived = true;
    }

    // EXTRADATA: base64 → [2B ushort prefix + GZip] → [100B rules][steamIds][bool enforceModList][string[] modListGuids]
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

            int offset = 100;
            if (offset + 2 > decompressed.Length) return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());
            int steamIdCount = BitConverter.ToUInt16(decompressed, offset);
            offset += 2;

            if (steamIdCount <= 0 || offset + steamIdCount * 8 > decompressed.Length)
                return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());

            var ids = new ulong[steamIdCount];
            for (int i = 0; i < steamIdCount; i++)
            {
                ids[i] = BitConverter.ToUInt64(decompressed, offset);
                offset += 8;
            }

            // enforceModList (1B bool)
            if (offset + 1 > decompressed.Length)
                return new ExtraData(ids, Array.Empty<string>());
            offset += 1;

            // modListGuids (string[]): ushort count + LiteNetLib Put(string) each
            if (offset + 2 > decompressed.Length)
                return new ExtraData(ids, Array.Empty<string>());
            int modCount = BitConverter.ToUInt16(decompressed, offset);
            offset += 2;

            var mods = new string[modCount];
            for (int i = 0; i < modCount; i++)
            {
                if (offset >= decompressed.Length) break;
                // LiteNetLib 7-bit encoded string length
                int strLen = 0;
                int shift = 0;
                while (offset < decompressed.Length)
                {
                    byte b = decompressed[offset++];
                    strLen |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                if (offset + strLen > decompressed.Length) break;
                mods[i] = System.Text.Encoding.UTF8.GetString(decompressed, offset, strLen);
                offset += strLen;
            }

            return new ExtraData(ids, mods);
        }
        catch
        {
            return new ExtraData(Array.Empty<ulong>(), Array.Empty<string>());
        }
    }

    private async Task FetchPlayerNames(IEnumerable<ulong> steamIds)
    {
        if (string.IsNullOrEmpty(_session?.WebApi)) return;

        ulong[] ids = steamIds.Where(id => !_nameCache.ContainsKey(id)).ToArray();
        if (ids.Length == 0) return;

        for (int i = 0; i < ids.Length; i += 100)
        {
            int batch = Math.Min(100, ids.Length - i);
            string joined = string.Join(",", ids.Skip(i).Take(batch));

            try
            {
                string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/"
                    + $"?key={_session.WebApi}&steamids={joined}";
                string resp = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);

                foreach (var player in doc.RootElement.GetProperty("response").GetProperty("players").EnumerateArray())
                {
                    ulong sid = ulong.Parse(player.GetProperty("steamid").GetString()!);
                    string name = player.GetProperty("personaname").GetString() ?? sid.ToString();
                    _nameCache[sid] = name;
                }
            }
            catch { }
        }
    }
}

internal sealed class SavedSteamSession
{
    public string AccountName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string WebApi { get; set; } = "";
}

internal sealed record ExtraData(ulong[] SteamIds, string[] Mods);
