using System.Collections.Concurrent;
using System.Linq;
using Discord;
using Discord.WebSocket;

namespace CasuMpServer;

internal sealed class DiscordBot
{
    private readonly DiscordSocketClient _client;
    private readonly ulong _channelId;
    private readonly ulong _consoleChannelId;
    private readonly string _token;
    private readonly ulong _adminUserId;
    private readonly Func<ulong, Task<string?>>? _avatarResolver;
    private readonly Action<string, string>? _onDiscordChat;   // 채팅 채널 -> 게임 (유저명, 텍스트 - Program.cs가 브로드캐스트)
    private readonly Action<string>? _onConsoleCommand;   // 콘솔 채널 -> 서버 콘솔 명령

    internal Action? OnReady { get; set; }
    private readonly Dictionary<ulong, string> _avatarCache = new();

    private const int NicknameCacheTtlSeconds = 300;
    private const int NicknameCacheCap = 500;
    private readonly Dictionary<(ulong GuildId, ulong UserId), (string? Nick, DateTime Fetched)> _nicknameCache = new();

    private const int ConsoleLogBatchChars = 1900;   // Discord 메시지 한도 여유
    private const int ConsoleLogQueueCap = 500;      // 오버플로 시 폐기 기준 (라인 수)
    private readonly ConcurrentQueue<string> _consoleLogQueue = new();
    private int _consoleLogDropped;

    internal DiscordBot(string token, ulong channelId, ulong consoleChannelId, ulong adminUserId,
        Action<string, string>? onDiscordChat = null, Action<string>? onConsoleCommand = null,
        Func<ulong, Task<string?>>? avatarResolver = null)
    {
        _token = token;
        _channelId = channelId;
        _consoleChannelId = consoleChannelId;
        _adminUserId = adminUserId;
        _avatarResolver = avatarResolver;
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

                int dropped = Interlocked.Exchange(ref _consoleLogDropped, 0);
                if (dropped > 0)
                {
                    await channel.SendMessageAsync(text: $"[console] … {dropped}줄 생략 (볼륨 초과)");
                }

                while (_consoleLogQueue.TryDequeue(out string? line))
                {
                    if (line == null) continue;
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
                WriteLog($"콘솔 로그 전송 실패: {ex.Message}");
            }
        }
    }

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

    internal async Task SendServerStatusAsync(bool started)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        var embed = new EmbedBuilder()
            .WithTitle(started ? "서버가 켜졌습니다." : "서버가 꺼졌습니다.")
            .WithColor(started ? Color.Green : Color.Red)
            .WithCurrentTimestamp()
            .Build();
        await channel.SendMessageAsync(embed: embed);
    }

    internal async Task SendLockdownAsync(bool started)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        var embed = new EmbedBuilder()
            .WithTitle(started ? "점검 모드가 시작되었습니다." : "점검 모드가 종료되었습니다.")
            .WithColor(started ? Color.Orange : Color.Green)
            .WithCurrentTimestamp()
            .Build();
        await channel.SendMessageAsync(embed: embed);
    }

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

    internal async Task SendCallAdminAsync(string playerName, ulong steamId)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string mention = BuildAdminMention();
        await channel.SendMessageAsync($"🚨 **{playerName}**의 호출!{mention}");
    }

    private string BuildAdminMention()
    {
        if (_adminUserId == 0) return "";
        bool isRole = _client.Guilds.Any(g => g.GetRole(_adminUserId) != null);
        return isRole ? $" <@&{_adminUserId}>" : $" <@{_adminUserId}>";
    }

    internal async Task SendDeathRespawnAsync(string playerName, ulong steamId, bool died, string layer)
    {
        if (!IsConnected || _channelId == 0) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        string? avatarUrl = steamId != 0 ? await FetchAvatarAsync(steamId) : null;
        var embed = new EmbedBuilder()
            .WithAuthor(name: $"{playerName}님이 {(died ? "사망했습니다." : "리스폰했습니다.")}",
                iconUrl: avatarUrl,
                url: steamId != 0 ? $"https://steamcommunity.com/profiles/{steamId}" : null)
            .WithDescription(died && !string.IsNullOrEmpty(layer) ? $"(L{layer})" : "")
            .WithFooter(text: steamId != 0 ? $"SteamID: {steamId}" : "")
            .WithColor(died ? Color.Red : Color.Green)
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
    }

    internal async Task SendChatAsync(string playerName, string message, string layer, string colorHex)
    {
        if (!IsConnected) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        await channel.SendMessageAsync(text: $"[{layer}] **{playerName}**: {message}");
    }

    internal async Task SendWebChatAsync(string message)
    {
        if (!IsConnected) return;
        var channel = await _client.GetChannelAsync(_channelId) as IMessageChannel;
        if (channel == null) return;

        await channel.SendMessageAsync(text: $"[WEB] {message}");
    }

    internal async Task UpdatePlayerCountTopicAsync(string text)
    {
        if (!IsConnected || _channelId == 0) return;
        try
        {
            var ch = await _client.GetChannelAsync(_channelId) as ITextChannel;
            if (ch == null) return;
            if (ch.Topic == text) return;
            await ch.ModifyAsync(p => p.Topic = text);
        }
        catch (Exception ex)
        {
            WriteLog($"채널 주제 갱신 실패: {ex.Message}");
        }
    }

    private async Task<string?> FetchAvatarAsync(ulong steamId)
    {
        if (_avatarCache.TryGetValue(steamId, out string? cached))
            return cached;

        if (_avatarResolver == null)
            return null;

        try
        {
            string? avatar = await _avatarResolver(steamId);
            if (!string.IsNullOrEmpty(avatar))
            {
                if (_avatarCache.Count > 500)
                    _avatarCache.Clear();
                _avatarCache[steamId] = avatar;
                return avatar;
            }
        }
        catch
        {
        }

        return null;
    }

    private static void WriteLog(string message)
    {
        CasuMpServer.Log.Info($"[server:bot] {message}");
    }

    private Task LogAsync(LogMessage msg)
    {
        if (msg.Severity <= LogSeverity.Verbose) return Task.CompletedTask;
        if (msg.Message?.Contains("Unknown Channel") == true) return Task.CompletedTask;
        if (msg.Exception is TaskCanceledException) return Task.CompletedTask;
        WriteLog(msg.ToString());
        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        WriteLog($"Ready — 길드 {_client.Guilds.Count}개, 채팅채널 {_channelId}, 콘솔채널 {_consoleChannelId}");

        if (_channelId != 0)
        {
            var ch = await _client.GetChannelAsync(_channelId);
            if (ch == null)
                WriteLog($"경고: 채팅 채널 {_channelId}을(를) 찾을 수 없습니다.");
        }

        if (_consoleChannelId != 0)
        {
            var ch3 = await _client.GetChannelAsync(_consoleChannelId);
            if (ch3 == null)
                WriteLog($"경고: 콘솔 채널 {_consoleChannelId}을(를) 찾을 수 없습니다.");
        }

        try
        {
            int deleted = 0;
            foreach (var cmd in await _client.GetGlobalApplicationCommandsAsync())
            {
                await cmd.DeleteAsync();
                deleted++;
            }
            foreach (var guild in _client.Guilds)
            {
                foreach (var cmd in await guild.GetApplicationCommandsAsync())
                {
                    await cmd.DeleteAsync();
                    deleted++;
                }
            }
            if (deleted > 0)
                WriteLog($"슬래시 명령어 {deleted}개 정리 완료 (글로벌 + 길드).");
        }
        catch (Exception ex)
        {
            WriteLog($"슬래시 명령어 정리 실패: {ex.Message}");
        }

        try
        {
            OnReady?.Invoke();
        }
        catch (Exception ex)
        {
            WriteLog($"Ready 콜백 실패: {ex.Message}");
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage msg)
    {
        if (msg.Author.IsBot) return Task.CompletedTask;
        if (msg.Content.StartsWith('/')) return Task.CompletedTask;

        string text = msg.Content.Trim();
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        if (_consoleChannelId != 0 && msg.Channel.Id == _consoleChannelId)
        {
            _onConsoleCommand?.Invoke(text);
            return Task.CompletedTask;
        }

        if (_channelId != 0 && msg.Channel.Id == _channelId)
        {
            _ = RelayDiscordChatAsync(msg);
        }
        return Task.CompletedTask;
    }

    private async Task RelayDiscordChatAsync(SocketMessage msg)
    {
        string name = await ResolveDisplayNameAsync(msg.Author, msg.Channel);
        _onDiscordChat?.Invoke(name, msg.Content.Trim());
    }

    private async Task<string> ResolveDisplayNameAsync(SocketUser author, ISocketMessageChannel channel)
    {
        string? nick = (author as SocketGuildUser)?.Nickname;
        if (!string.IsNullOrEmpty(nick)) return nick;

        if (channel is IGuildChannel gc)
        {
            try
            {
                string? cached = GetCachedNickname(gc.GuildId, author.Id);
                if (cached != null) return cached;

                var member = await gc.Guild.GetUserAsync(author.Id);
                string? fetched = member?.Nickname;
                SetCachedNickname(gc.GuildId, author.Id, fetched);
                if (!string.IsNullOrEmpty(fetched)) return fetched;
            }
            catch
            {
            }
        }
        return author.Username;
    }

    private string? GetCachedNickname(ulong guildId, ulong userId)
    {
        if (_nicknameCache.TryGetValue((guildId, userId), out var entry)
            && DateTime.UtcNow - entry.Fetched < TimeSpan.FromSeconds(NicknameCacheTtlSeconds))
        {
            return entry.Nick;
        }
        return null;
    }

    private void SetCachedNickname(ulong guildId, ulong userId, string? nick)
    {
        if (_nicknameCache.Count > NicknameCacheCap)
            _nicknameCache.Clear();
        _nicknameCache[(guildId, userId)] = (nick, DateTime.UtcNow);
    }
}
