using System.Collections.Concurrent;
using System.Text.Json;
using Discord;
using Discord.WebSocket;

namespace CasuMpOrchestrator;

/// <summary>Discord 관리 봇 (D1) — 구 시스템(project-old DiscordBot.cs) 이식.
/// 단일 채널(DiscordChannelId)에 접속/퇴장·마이그레이션·관리자 호출·사망/리스폰·채팅을
/// 알림하고, 채널 메시지를 게임 채팅으로 전달한다 (speaker="관리자" — 전 인스턴스 브로드캐스트).
/// 토큰/채널 미설정 시 비활성 (StartAsync가 no-op). Steam ID는 현행 steamEnabled=false
/// 환경에서 0이므로 아바타/프로필 링크는 steamId ≠ 0일 때만 동작한다.</summary>
internal sealed class DiscordBot
{
    private readonly DiscordSocketClient _client;
    private readonly ulong _channelId;
    private readonly ulong _consoleChannelId;
    private readonly string _token;
    private readonly string _steamApiKey;
    private readonly ulong _adminUserId;
    private readonly Action<string>? _onDiscordChat;      // 채팅 채널 → 게임 (Program.cs가 브로드캐스트)
    private readonly Action<string>? _onConsoleCommand;   // 콘솔 채널 → 서버 콘솔 명령
    private readonly Dictionary<ulong, string> _avatarCache = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // ── 콘솔 로그 릴레이 (D1) — 전체 콘솔 로그를 콘솔 채널로 배치 전송 ──
    private const int ConsoleLogBatchChars = 1900;   // Discord 메시지 한도 여유
    private const int ConsoleLogQueueCap = 500;      // 오버플로 시 폐기 기준 (라인 수)
    private readonly ConcurrentQueue<string> _consoleLogQueue = new();
    private int _consoleLogDropped;

    internal DiscordBot(string token, ulong channelId, ulong consoleChannelId, string steamApiKey, ulong adminUserId,
        Action<string>? onDiscordChat = null, Action<string>? onConsoleCommand = null)
    {
        _token = token;
        _channelId = channelId;
        _consoleChannelId = consoleChannelId;
        _steamApiKey = steamApiKey;
        _adminUserId = adminUserId;
        _onDiscordChat = onDiscordChat;
        _onConsoleCommand = onConsoleCommand;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
        });

        _client.Log += LogAsync;
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
    }

    internal async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_token)) return;
        if (_channelId == 0 && _consoleChannelId == 0) return;
        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
        _ = Task.Run(ConsoleLogSenderLoopAsync);
    }

    internal async Task StopAsync()
    {
        if (_client.ConnectionState == ConnectionState.Connected)
            await _client.StopAsync();
    }

    internal bool IsConnected => _client.ConnectionState == ConnectionState.Connected;

    /// <summary>전체 콘솔 로그 라인 접수 (TimestampedConsoleWriter.OnConsoleLine — 접두사 포함).
    /// 빈 줄 제외, 큐 오버플로 시 오래된 라인 폐기 + 생략 카운터 누적.</summary>
    internal void EnqueueConsoleLog(string line)
    {
        if (_consoleChannelId == 0) return;
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_consoleLogQueue.Count >= ConsoleLogQueueCap)
        {
            _consoleLogQueue.TryDequeue(out _);
            Interlocked.Increment(ref _consoleLogDropped);
        }
        _consoleLogQueue.Enqueue(line);
    }

    /// <summary>콘솔 로그 배치 전송 루프 — 1초 주기로 큐를 비워 최대 1900자 단위 메시지로
    /// 콘솔 채널에 전송한다 (연결 전 라인은 큐에 쌓였다가 연결 후 일괄 전송).</summary>
    private async Task ConsoleLogSenderLoopAsync()
    {
        var pending = new List<string>();
        while (true)
        {
            try
            {
                Thread.Sleep(1000);

                if (!IsConnected || _consoleChannelId == 0) continue;
                var channel = await _client.GetChannelAsync(_consoleChannelId) as IMessageChannel;
                if (channel == null) continue;

                // 생략 마커 선행 (누적된 경우).
                int dropped = Interlocked.Exchange(ref _consoleLogDropped, 0);
                if (dropped > 0)
                {
                    await channel.SendMessageAsync(text: $"[console] … {dropped}줄 생략 (볼륨 초과)");
                }

                while (_consoleLogQueue.TryDequeue(out string? line))
                {
                    if (line == null) continue;
                    // 개별 라인이 한도 초과 시 잘라서 전송 (경계 안전).
                    if (line.Length > ConsoleLogBatchChars)
                        line = line[..ConsoleLogBatchChars] + " …";

                    if (pending.Count > 0 && pending.Sum(p => p.Length + 1) + line.Length > ConsoleLogBatchChars)
                    {
                        await channel.SendMessageAsync(text: string.Join("\n", pending));
                        pending.Clear();
                    }
                    pending.Add(line);
                }
                if (pending.Count > 0)
                {
                    await channel.SendMessageAsync(text: string.Join("\n", pending));
                    pending.Clear();
                }
            }
            catch (Exception ex)
            {
                Log($"콘솔 로그 전송 실패: {ex.Message}");
            }
        }
    }

    /// <summary>접속/퇴장 알림 — Steam 아바타 + 프로필 링크 (steamId ≠ 0일 때만).</summary>
    internal async Task SendJoinLeaveAsync(string playerName, ulong steamId, bool joined)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        var title = joined ? "접속" : "퇴장";
        string? avatarUrl = steamId != 0 ? await FetchAvatarAsync(steamId) : null;

        var embed = new EmbedBuilder()
            .WithAuthor(name: $"{playerName}님이 {title}했습니다.",
                iconUrl: avatarUrl,
                url: steamId != 0 ? $"https://steamcommunity.com/profiles/{steamId}" : null)
            .WithFooter(text: steamId != 0 ? $"SteamID: {steamId}" : "")
            .WithColor(joined ? Color.Green : Color.Red)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    /// <summary>레이어 이동(마이그레이션 커밋) 알림 — L{from} > L{to}.</summary>
    internal async Task SendMigrationAsync(string playerName, ulong steamId, int fromDepth, int toDepth)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string? avatarUrl = steamId != 0 ? await FetchAvatarAsync(steamId) : null;
        var embed = new EmbedBuilder()
            .WithAuthor(name: $"{playerName}님이 레이어를 이동합니다.",
                iconUrl: avatarUrl,
                url: steamId != 0 ? $"https://steamcommunity.com/profiles/{steamId}" : null)
            .WithDescription($"L{fromDepth} > L{toDepth}")
            .WithFooter(text: steamId != 0 ? $"SteamID: {steamId}" : "")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    /// <summary>관리자 호출 알림 — DiscordAdminUserId가 설정된 경우 멘션.</summary>
    internal async Task SendCallAdminAsync(string playerName, ulong steamId)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string mention = _adminUserId != 0 ? $" <@{_adminUserId}>" : "";
        await channel.SendMessageAsync($"🚨 **{playerName}**의 호출!{mention}");
    }

    /// <summary>사망/리스폰 알림.</summary>
    internal async Task SendDeathRespawnAsync(string playerName, ulong steamId, bool died, string layer)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string? avatarUrl = steamId != 0 ? await FetchAvatarAsync(steamId) : null;
        var embed = new EmbedBuilder()
            .WithAuthor(name: $"{playerName}님이 {(died ? "사망하였습니다." : "리스폰하였습니다.")}",
                iconUrl: avatarUrl,
                url: steamId != 0 ? $"https://steamcommunity.com/profiles/{steamId}" : null)
            .WithDescription(died && !string.IsNullOrEmpty(layer) ? $"(L{layer})" : "")
            .WithFooter(text: steamId != 0 ? $"SteamID: {steamId}" : "")
            .WithColor(died ? Color.Red : Color.Green)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    /// <summary>게임 채팅 → Discord — "[L{n}] **이름**: 메시지".</summary>
    internal async Task SendChatAsync(string playerName, string message, string layer, string colorHex)
    {
        if (!IsConnected) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        await channel.SendMessageAsync(text: $"[{layer}] **{playerName}**: {message}");
    }

    /// <summary>투표 시작 알림.</summary>
    internal async Task SendVoteStartAsync(string kind, string title, string promptBody, float timeoutSeconds)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        var embed = new EmbedBuilder()
            .WithTitle($"🗳️ {title}")
            .WithDescription(promptBody)
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    /// <summary>투표 결과 알림.</summary>
    internal async Task SendVoteResultAsync(string kind, string title, int yes, int no, int ignore,
        bool passed, string? detail)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string verdict = passed ? "✅ 가결" : "❌ 부결";
        var embed = new EmbedBuilder()
            .WithTitle($"🗳️ {title} — {verdict}")
            .WithDescription($"{title} 투표 결과")
            .AddField("찬성", yes.ToString(), inline: true)
            .AddField("반대", no.ToString(), inline: true)
            .AddField("기권", ignore.ToString(), inline: true)
            .WithColor(passed ? Color.Green : Color.Red)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    /// <summary>투표 거부 알림.</summary>
    internal async Task SendVoteRejectedAsync(string kind, string title, string reason)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        var embed = new EmbedBuilder()
            .WithTitle($"🗳️ {title} 거부")
            .WithDescription($"**{title}**\n사유: {reason}")
            .WithColor(Color.LightGrey)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    // ── Steam Avatar ────────────────────────────────────────────────────

    private async Task<string?> FetchAvatarAsync(ulong steamId)
    {
        if (_avatarCache.TryGetValue(steamId, out string? cached))
            return cached;

        if (string.IsNullOrEmpty(_steamApiKey))
            return null;

        try
        {
            string url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/"
                + $"?key={_steamApiKey}&steamids={steamId}";
            var response = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            var player = doc.RootElement
                .GetProperty("response")
                .GetProperty("players")
                .EnumerateArray()
                .FirstOrDefault();

            if (player.ValueKind != JsonValueKind.Undefined)
            {
                string avatar = player.GetProperty("avatarfull").GetString() ?? "";
                if (!string.IsNullOrEmpty(avatar))
                {
                    if (_avatarCache.Count > 500)
                        _avatarCache.Clear();
                    _avatarCache[steamId] = avatar;
                    return avatar;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    // ── Event Handlers ─────────────────────────────────────────────────

    /// <summary>봇 자체 로그 — 표준 콘솔 규격 "[HH:mm:ss orch:bot] ..."으로 출력하고
    /// 릴레이 라인으로도 수집되므로 콘솔 채널 로그 릴레이에 자연히 포함된다.</summary>
    private static void Log(string message)
    {
        if (TimestampedConsoleWriter.Instance != null)
            TimestampedConsoleWriter.Instance.PrintRelayed("orch:bot", message);
        else
            ConsoleIO.WriteLine($"[DiscordBot] {message}");
    }

    private Task LogAsync(LogMessage msg)
    {
        if (msg.Severity <= LogSeverity.Verbose) return Task.CompletedTask;
        if (msg.Message?.Contains("Unknown Channel") == true) return Task.CompletedTask;
        if (msg.Exception is TaskCanceledException) return Task.CompletedTask;
        Log(msg.ToString());
        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        Log($"Ready — 길드 {_client.Guilds.Count}개, 채팅채널 {_channelId}, 콘솔채널 {_consoleChannelId}");

        if (_channelId != 0)
        {
            var ch = await _client.GetChannelAsync(_channelId);
            if (ch == null)
                Log($"경고: 채팅 채널 {_channelId}을(를) 찾을 수 없습니다.");
        }

        if (_consoleChannelId != 0)
        {
            var ch3 = await _client.GetChannelAsync(_consoleChannelId);
            if (ch3 == null)
                Log($"경고: 콘솔 채널 {_consoleChannelId}을(를) 찾을 수 없습니다.");
        }

        // Delete all previously registered guild slash commands (legacy cleanup).
        try
        {
            ulong guildId = 0;
            var ch2 = await _client.GetChannelAsync(_channelId);
            if (ch2 is IGuildChannel guildCh)
                guildId = guildCh.GuildId;

            if (guildId != 0)
            {
                var guild = await _client.Rest.GetGuildAsync(guildId);
                var existing = await guild.GetApplicationCommandsAsync();
                if (existing.Count > 0)
                {
                    foreach (var cmd in existing)
                        await cmd.DeleteAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"슬래시 명령어 정리 실패: {ex.Message}");
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage msg)
    {
        if (msg.Author.IsBot) return Task.CompletedTask;
        if (msg.Content.StartsWith('/')) return Task.CompletedTask;

        string text = msg.Content.Trim();
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        // 콘솔 채널 우선 — 채널이 동일하게 설정된 경우 콘솔 명령 경로가 승리한다.
        if (_consoleChannelId != 0 && msg.Channel.Id == _consoleChannelId)
        {
            _onConsoleCommand?.Invoke(text);
            return Task.CompletedTask;
        }

        if (_channelId != 0 && msg.Channel.Id == _channelId)
        {
            _onDiscordChat?.Invoke(text);
        }
        return Task.CompletedTask;
    }
}
