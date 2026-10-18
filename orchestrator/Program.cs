using CasuMp.Protocol;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace CasuMpOrchestrator;

internal static class Program
{
    private static bool _runActive;   // 런 수명주기: DORMANT(false) ↔ ACTIVE(true)

    private static void Main(string[] args)
    {
        Log.Init(null);

        string configPath = args.Length > 0 ? args[0] : "orchestrator.json";
        OrchestratorConfig config = OrchestratorConfig.Load(configPath);

        Log.Info($"구성 로드: {configPath}");
        Log.Info($"제어 허브 포트: {config.Port}");

        using var cts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

        var hub = new ControlHub(config, cts.Token);
        var runRules = new RunRuleStore(config.RunTablePath, config.RuleTablePath);
        var instances = new InstanceManager(config, hub);
        var sessions = new PlayerSessionStore(config, hub, instances);
        var dataStore = new PlayerDataStore(config);
        var migrations = new MigrationCoordinator(config, hub, instances, sessions, dataStore);

        var banList = new BanList(config.BanListPath);

        var votes = new VoteCoordinator(graceSeconds: 5);
        var voteService = new VoteService(hub, sessions, votes, banList);

        hub.OnConnectionClosed += conn =>
        {
            switch (conn.Kind)
            {
                case ClientKind.Gateway:
                    Log.Info("게이트웨이 연결 종료 — 재연결 대기.");
                    break;
                case ClientKind.Agent:
                    instances.OnAgentConnectionClosed(conn);
                    break;
                case ClientKind.Mod:
                    instances.OnModConnectionClosed(conn);
                    break;
            }
        };

        hub.Start();

        LobbyMetadataService lobby = null!;
        var console = new OperatorConsole(hub, sessions, instances, migrations,
            (key, unban) =>
            {
                bool banned = unban != "unban";
                banList.Toggle(key, banned);
                hub.Send(hub.GatewayConnection, MessageTypes.Ban, new { playerKey = key, banned });
            },
            (key, reason) => hub.Send(hub.GatewayConnection, MessageTypes.Kick, new { playerKey = key, reason }),
            runRules,
            () =>
            {
                PushRunRulesToInstances(hub, runRules);
                lobby.Push(force: true);
            },
            command =>
            {
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, MessageTypes.Console, new { command });
                }
            },
            config: config, banList: banList, configPath: configPath);
        console.Start();
        var discordBot = new DiscordBot(config.DiscordBotToken, config.DiscordChannelId,
            config.DiscordConsoleChannelId, config.SteamWebApiKey, config.DiscordAdminUserId,
            (username, text) =>
            {
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, MessageTypes.Chat, new { speaker = username, message = text, color = "", layer = "", prefix = "D", prefixColor = "#5865F2" });
                }
            },
            text =>
            {
                console.SubmitLine(text);
            });
        lobby = new LobbyMetadataService(hub, runRules, sessions, discordBot);
        var gatewayHandler = new GatewayMessageHandler(config, hub, sessions, migrations, runRules, discordBot, lobby);
        var modHandler = new ModMessageHandler(hub, sessions, instances, migrations, dataStore, runRules, voteService, discordBot, lobby);

        discordBot.OnReady = () =>
        {
            lobby.RefreshTopic();
            _ = discordBot.SendServerStatusAsync(started: true);
        };
        console.LockdownNotify = on =>
            _ = on ? discordBot.SendLockdownAsync(started: true) : discordBot.SendLockdownAsync(started: false);
        Log.SetRelay(discordBot.EnqueueConsoleLog);

        migrations.MigrationCommitted += (player, fromDepth, toDepth, isRespawn) =>
        {
            var state = sessions.Get(player);
            string name = state?.Username ?? player.Value;
            ulong steamId = LobbyMetadataService.SteamIdOf(player);

            if (isRespawn)
            {
                _ = discordBot.SendDeathRespawnAsync(name, steamId, died: false, layer: "");

                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, MessageTypes.Announce, new
                    {
                        kind = "respawn",
                        playerKey = player.Value,
                        name,
                        fromDepth = 0,
                        toDepth = 0,
                    });
                }
            }
            else
            {
                _ = discordBot.SendMigrationAsync(name, steamId, fromDepth, toDepth);
                lobby.Push(force: true);

                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, MessageTypes.Announce, new
                    {
                        kind = "migration",
                        playerKey = player.Value,
                        name,
                        fromDepth,
                        toDepth,
                    });
                }
            }

            lobby.RefreshTopic();
        };

        _ = discordBot.StartAsync();

        while (!shutdownCts.IsCancellationRequested)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, voteService, discordBot, lobby, gatewayHandler, modHandler, conn, msg);
                }
                catch (Exception ex)
                {
                    Log.Info($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            migrations.Tick();
            lobby.Push();

            voteService.Tick();
            instances.TickPrewarm();
            instances.TickIdle(DateTime.UtcNow, key =>
                sessions.All.Count(s => s.InstanceId == key && s.Session != PlayerSessionState.Offline)
                + migrations.CountTargeting(key));
            TickRunLifecycle(sessions);
            console.Tick();
            Thread.Sleep(20);
        }

        Log.Info("종료 중 — 데이터 저장 후 스택 종료...");

        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, MessageTypes.Shutdown, null);
        }
        hub.SendNoAck(hub.GatewayConnection, MessageTypes.Shutdown, null);
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Agent && !c.Closed))
        {
            hub.SendNoAck(conn, MessageTypes.Shutdown, null);
        }

        DateTime shutdownDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < shutdownDeadline)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, voteService, discordBot, lobby, gatewayHandler, modHandler, conn, msg);
                }
                catch (Exception ex)
                {
                    Log.Info($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            Thread.Sleep(20);
        }

        sessions.PersistAllOffline(); // 재시작 시 스테일 온라인 복원 방지

        try { discordBot.SendServerStatusAsync(started: false).GetAwaiter().GetResult(); } catch { }

        Log.Info("종료 완료.");
        try { discordBot.StopAsync().GetAwaiter().GetResult(); } catch { }
        hub.Stop();
        cts.Cancel();
    }

    private static void TickRunLifecycle(PlayerSessionStore sessions)    {
        int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
        if (online > 0 && !_runActive)
        {
            _runActive = true;
        }
        else if (online == 0 && _runActive)
        {
            _runActive = false;
        }
    }

    private static void Dispatch(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions,
        InstanceManager instances, MigrationCoordinator migrations, PlayerDataStore dataStore,
        BanList banList, RunRuleStore runRules, VoteService voteService, DiscordBot discordBot,
        LobbyMetadataService lobby, GatewayMessageHandler gatewayHandler, ModMessageHandler modHandler,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.GatewayHello:
                if (conn.Kind != ClientKind.Unknown) break;
                conn.Kind = ClientKind.Gateway;
                conn.GatewayVersion = msg.PayloadAs<HelloVersion>()?.Version;
                sessions.PushTableSnapshot();
                hub.SendNoAck(conn, MessageTypes.AuthInfo, // 밴/서버 설정 푸시
                    new { serverName = config.ServerName, serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                hub.SendNoAck(conn, MessageTypes.AckReply, null);
                lobby.Push(force: true);
                if (LockdownState.Active)
                {
                    hub.SendNoAck(conn, MessageTypes.Maintenance, new
                    {
                        On = true,
                        Message = "Server is in maintenance mode",
                        KickAll = false,
                        Bypass = LockdownState.Bypass,
                    });
                    hub.SendNoAck(conn, MessageTypes.AuthInfo,
                        new { serverName = config.ServerName + " [MNT]", serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                }
                return;

            case MessageTypes.AgentHello:
                if (conn.Kind != ClientKind.Unknown) break;
                var agent = msg.PayloadAs<AgentHelloPayload>();
                if (agent == null) break;
                conn.Kind = ClientKind.Agent;
                conn.MachineId = agent.MachineId;
                conn.AgentCapacity = agent.Capacity;
                conn.AgentAddress = agent.Address;
                instances.RegisterAgent(agent.MachineId, agent.Capacity, agent.Address);
                instances.PrewarmLayers(); // 레이어1 프리웜
                hub.SendNoAck(conn, MessageTypes.AgentHelloAck, new { ok = true });
                return;

            case MessageTypes.ModHello:
                if (conn.Kind != ClientKind.Unknown) break;
                var mod = msg.PayloadAs<ModHelloPayload>();
                if (mod == null) break;
                conn.Kind = ClientKind.Mod;
                conn.InstanceKey = mod.InstanceKey;
                conn.InstancePort = mod.Port;
                conn.InstanceDepth = mod.Depth;
                instances.OnModHello(conn, mod); // 인스턴스 연결 등록/READY 보고
                hub.SendNoAck(conn, MessageTypes.ModHelloAck, new { ok = true });
                hub.SendNoAck(conn, MessageTypes.RunRulesState, // 규칙 초기 푸시
                    new { runSettings = runRules.RunSnapshot, rules = runRules.RuleSnapshot });
                return;
        }

        if (conn.Kind == ClientKind.Unknown)
        {
            return;
        }

        if (msg.Type == MessageTypes.Log)
        {
            LogPayload? log = msg.PayloadAs<LogPayload>();
            if (log != null && !string.IsNullOrEmpty(log.Message))
            {
                Log.Info($"[{log.Source}] {log.Message}");
            }
            return;
        }

        switch (conn.Kind)
        {
            case ClientKind.Gateway:
                gatewayHandler.Dispatch(conn, msg);
                break;
            case ClientKind.Agent:
                DispatchAgent(instances, sessions, conn, msg);
                break;
            case ClientKind.Mod:
                modHandler.Dispatch(conn, msg);
                break;
        }
    }

    private static void DispatchAgent(InstanceManager instances, PlayerSessionStore sessions,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.InstanceExited:
                if (msg.PayloadAs<InstanceExitedPayload>() is { } exited)
                {
                    instances.OnInstanceExited(exited.InstanceKey, exited.Code);
                    sessions.OnInstanceExited(exited.InstanceKey);
                }
                break;
            default:
                break;
        }
    }

    internal static bool TryPlayerKey(ControlMessage msg, out PlayerKey key)
    {
        key = default;
        string? value = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    internal static void PushRunRulesToInstances(ControlHub hub, RunRuleStore runRules)
    {
        var runSettings = runRules.RunSnapshot;
        var rules = runRules.RuleSnapshot;
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, MessageTypes.RunRulesState, new { runSettings, rules });
        }
    }

    internal static bool TryPlayerKey(string? value, out PlayerKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    internal static int? GetEpoch(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("epoch", out JsonElement e) && e.TryGetInt32(out int v) ? v : null;
    }

    internal static string? GetUsername(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("username", out JsonElement u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }

}

public sealed class BanList
{
    private readonly string _path;
    private readonly HashSet<string> _banned = new();

    public BanList(string path)
    {
        _path = path;
        Load();
    }

    public void Toggle(string playerKey, bool banned)
    {
        if (banned) _banned.Add(playerKey);
        else _banned.Remove(playerKey);
        Save();
    }

    public IReadOnlyCollection<string> All => _banned.ToList();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (string key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [])
                _banned.Add(key);
        }
        catch { }
    }

    private void Save()
    {
        try { AtomicJsonFile.Write(_path, JsonSerializer.Serialize(_banned.ToArray())); }
        catch { }
    }
}
