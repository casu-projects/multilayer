using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using SteamKit2;

namespace SteamLobbyBrowser;

internal sealed class LobbyBrowser
{
    private const uint AppId = 4576510;

    // 신버전(5.x) 로비 키 = "COOP_MOD_" + suffix, 구버전(4.x) = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_" + suffix
    private const string KeyPrefixNew = "COOP_MOD_";
    private const string KeyPrefixOld = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_COOP_MOD_";

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

    // 신/구 공용 메타데이터 조회 - 신 키 우선, 없으면 구 키로 폴백
    private static string? Meta(IReadOnlyDictionary<string, string> md, string suffix)
    {
        if (md.TryGetValue(KeyPrefixNew + suffix, out string? v) && !string.IsNullOrEmpty(v))
            return v;
        md.TryGetValue(KeyPrefixOld + suffix, out string? old);
        return old;
    }

    // EXTRADATA 규칙 구조체 크기: 신버전 112B(deathcounter+rules 108), 구버전 100B(deathcounter+rules 96)
    private static int ExtraDataOffset(IReadOnlyDictionary<string, string> md) =>
        md.ContainsKey(KeyPrefixNew + SuffixVersion) ? 112 : 100;

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamMatchmaking? _matchmaking;
    private readonly HttpClient _http = new();

    private bool _loggedOn;
    private bool _lobbyListReceived;
    private SavedSteamSession? _session;
    private Dictionary<ulong, string> _nameCache = new();
    private string? _versionFilter;

    public LobbyBrowser() { }

    public async Task RunAsync(string sessionPath, string? versionFilter, CancellationToken ct)
    {
        _session = LoadSession(sessionPath);
        _versionFilter = string.IsNullOrWhiteSpace(versionFilter) ? null : versionFilter.Trim();

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

        // --filter: 버전 태그에 지정 문자열이 포함된 로비만 (신/구 포맷 공통)
        IReadOnlyList<SteamMatchmaking.Lobby> lobbies = callback.Lobbies;
        if (_versionFilter != null)
        {
            lobbies = callback.Lobbies
                .Where(l => (Meta(l.Metadata, SuffixVersion) ?? "").Contains(_versionFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Console.WriteLine($"Filter: version contains \"{_versionFilter}\"");
            if (lobbies.Count == 0)
            {
                Console.WriteLine("No lobbies match the version filter.");
                _lobbyListReceived = true;
                return;
            }
        }

        int totalPlayers = lobbies.Sum(l =>
        {
            string? pc = Meta(l.Metadata, SuffixPlrCount);
            return int.TryParse(pc, out int n) ? n : 0;
        });
        Console.WriteLine($"\nTotal {lobbies.Count} lobbies, {totalPlayers} players\n");

        var allSteamIds = new HashSet<ulong>();
        foreach (var lobby in lobbies)
        {
            string? extraBase64 = Meta(lobby.Metadata, SuffixExtraData);
            if (!string.IsNullOrEmpty(extraBase64))
            {
                foreach (ulong id in DecodeExtraData(extraBase64, ExtraDataOffset(lobby.Metadata)).SteamIds)
                    allSteamIds.Add(id);
            }
        }

        if (allSteamIds.Count > 0 && !string.IsNullOrEmpty(_session?.WebApi))
        {
            await FetchPlayerNames(allSteamIds);
        }

        int index = 1;
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

            string pwIcon = hasPassword == "1" ? " \uf023" : "";
            string dedIcon = isDedicated == "1" ? " \uf108" : "";
            string lobbyLabel = string.IsNullOrEmpty(lobbyName) ? "(No Name)" : lobbyName;

            string? plrCount = Meta(lobby.Metadata, SuffixPlrCount);
            string plrDisplay = int.TryParse(plrCount, out int pc) ? pc.ToString() : "0";

            Console.WriteLine($"#{index} {lobbyLabel}{pwIcon}{dedIcon} <{lobby.SteamID.ConvertToUInt64()}>");
            if (!string.IsNullOrEmpty(description))
            {
                string[] dlines = description.Replace("\r", "").Split('\n');
                Console.WriteLine($"    Desc:      {dlines[0]}");
                for (int d = 1; d < dlines.Length; d++)
                    Console.WriteLine($"               {dlines[d]}");
            }
            if (!string.IsNullOrEmpty(version)) Console.WriteLine($"    Version:   {version}");
            if (!string.IsNullOrEmpty(gamemode)) Console.WriteLine($"    Mode:      {gamemode}");
            if (!string.IsNullOrEmpty(currentLayer)) Console.WriteLine($"    Layer:     {currentLayer}");
            if (!string.IsNullOrEmpty(depth)) Console.WriteLine($"    Depth:     {depth}");
            if (!string.IsNullOrEmpty(language)) Console.WriteLine($"    Lang:      {language}");
            Console.WriteLine($"    Players:   {plrDisplay}/{lobby.MaxMembers}");

            ExtraData? extraData = null;
            string? extraB64 = Meta(lobby.Metadata, SuffixExtraData);
            if (!string.IsNullOrEmpty(extraB64))
            {
                extraData = DecodeExtraData(extraB64, ExtraDataOffset(lobby.Metadata));
                foreach (ulong sid in extraData.SteamIds)
                {
                    string name = _nameCache.TryGetValue(sid, out string? n) ? n : sid.ToString();
                    Console.WriteLine($"    \u2514 {name} <{sid}>");
                }
            }

            if (!string.IsNullOrEmpty(livingCount)) Console.WriteLine($"    Survived:  {livingCount}/{plrDisplay}");
            if (!string.IsNullOrEmpty(avgMood)) Console.WriteLine($"    AvgMood:   {avgMood}");

            // mods: EXTRADATA 우선, 없으면 신버전 COOP_MOD_MODS 키(': ' 구분) 사용
            string[] mods = extraData?.Mods ?? Array.Empty<string>();
            if (mods.Length == 0 && !string.IsNullOrEmpty(modsKey))
                mods = modsKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (mods.Length > 0)
            {
                Console.WriteLine($"    Mods:      {mods.Length}");
                foreach (string mod in mods)
                    Console.WriteLine($"    \u2514 {mod}");
            }

            Console.WriteLine();
            index++;
        }

        _lobbyListReceived = true;
    }

    // EXTRADATA: base64 → [2B ushort prefix + GZip] → [rules struct][steamIds][bool enforceModList][string[] modListGuids]
    // rulesOffset: 신버전 112B / 구버전 100B (ExtraDataOffset 로 판정)
    private static ExtraData DecodeExtraData(string base64, int rulesOffset)
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

            int offset = rulesOffset;
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
