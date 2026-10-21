using CasuMp.Protocol;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CasuMpServer;

internal static class Program
{
    private static bool _runActive; // 런 수명주기: DORMANT(false) ↔ ACTIVE(true)

    private static void Main(string[] args)
    {
        Log.Init(null);

        string configPath = args.Length > 0 ? args[0] : "server.json";
        ServerConfig config = ServerConfig.Load(configPath);

        Log.Info($"구성 로드: {configPath}");
        Log.Info($"제어 허브 포트: {config.Port}");
        Log.Info($"직접연결 포트: {config.DirectListenPort}");

        using var cts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

        var hub = new ControlHub(config, cts.Token);
        var mapStore = new WorldSnapshotStore();
        var playerDetails = new PlayerDetailRegistry();
        var objectBackgrounds = new ObjectBackgroundStore();
        var objectQueries = new RequestRegistry<MapObjectQueryResponse>();
        var objectDetails = new RequestRegistry<MapObjectDetailResponse>();
        var runRules = new RunRuleStore(config.RunTablePath, config.RuleTablePath);
        var instances = new InstanceManager(config, hub);
        var gateway = new ClientGateway(config);
        gateway.SetPrewarmGate(true);
        hub.OutboundFilter = (conn, type) =>
            type == MessageType.SHUTDOWN
            || conn.Kind != ClientKind.Mod || conn.InstanceKey == null
            || instances.CanSendModCommand(conn.InstanceKey, type);
        var sessions = new PlayerSessionStore(config, instances);
        instances.InstanceResumed += key => sessions.PushRoutesForInstance(key);
        var dataStore = new PlayerDataStore(config);
        var migrations = new MigrationCoordinator(config, hub, gateway, instances, sessions, dataStore);

        var banList = new BanList(config.BanListPath);

        hub.OnConnectionClosed += conn =>
        {
            switch (conn.Kind)
            {
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
        var console = new OperatorConsole(hub, gateway, sessions, instances, migrations,
            (key, unban) =>
            {
                bool banned = unban != "unban";
                banList.Toggle(key, banned);
                gateway.SetBanned(PlayerKey.FromString(key), banned);
            },
            (key, reason) => gateway.Kick(PlayerKey.FromString(key), reason ?? "Kicked."),
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
                    hub.SendNoAck(conn, MessageType.CONSOLE, new { command });
                }
            },
            pushLobbyAction: () => lobby.Push(force: true),
            config: config, configPath: configPath);
        console.Start();
        SteamApiClient? steamApi = null;
        var discordBot = new DiscordBot(config.DiscordBotToken, config.DiscordChannelId,
            config.DiscordConsoleChannelId,
            (username, text) =>
            {
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, MessageType.CHAT, new { speaker = username, message = text, color = "", layer = "", prefix = "D", prefixColor = "#5865F2" });
                }
            },
            text =>
            {
                console.SubmitLine(text);
            },
            avatarResolver: steamId => steamApi?.GetAvatarAsync(steamId) ?? Task.FromResult<string?>(null));

        instances.InstanceSuspended += (key, reason) =>
        {
            if (reason == "prewarm" && !instances.AdmissionEverOpened) return;
            InstanceInfo? info = instances.Find(key);
            if (info != null) _ = discordBot.SendLayerSuspendedAsync(info.Depth, reason);
        };
        instances.InstanceResumed += key =>
        {
            InstanceInfo? info = instances.Find(key);
            if (info != null) _ = discordBot.SendLayerResumedAsync(info.Depth);
        };

        if (config.SteamEnabled)
        {
            try
            {
                steamApi = new SteamApiClient(config.SteamApiUrl, config.SteamApiToken);
            }
            catch (Exception ex)
            {
                Log.Info($"Steam API 클라이언트 비활성화: {ex.Message}");
            }
        }

        lobby = new LobbyMetadataService(gateway, runRules, sessions, discordBot, steamApi);
        var lifecycle = new SessionLifecycle(gateway, sessions, migrations, lobby, discordBot, hub);
        gateway.SetLifecycle(lifecycle);
        sessions.RouteReady += gateway.ApplyRoute;
        sessions.KickRequested += (key, reason) => gateway.Kick(key, reason);
        gateway.SetBans(banList.All);
        gateway.UpdateServerInfo(config.ServerName, config.ServerPassword, config.MaxPlayers);

        if (LockdownState.Active)
        {
            gateway.SetMaintenance(true, "Server is in maintenance mode", LockdownState.Bypass, kickAll: false);
            gateway.UpdateServerInfo(config.ServerName + " [MNT]", config.ServerPassword, config.MaxPlayers);
        }

        var modHandler = new ModMessageHandler(hub, sessions, instances, migrations, dataStore, runRules, discordBot, lobby, mapStore, playerDetails, objectBackgrounds, objectQueries, objectDetails);

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
                    hub.SendNoAck(conn, MessageType.ANNOUNCE, new
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
                    hub.SendNoAck(conn, MessageType.ANNOUNCE, new
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

        MapWebServer? mapWeb = null;
        if (config.WebEnabled)
        {
            try
            {
                mapWeb = MapWebServer.Create(config, mapStore, steamApi, hub, playerDetails, objectBackgrounds, objectQueries, objectDetails, instances);
                mapWeb.StartAsync().GetAwaiter().GetResult();
                Log.Info($"맵 웹 시작: {config.WebListen}");
            }
            catch (Exception ex)
            {
                Log.Info($"맵 웹 시작 실패: {ex.Message}");
                mapWeb = null;
            }
        }

        var direct = new DirectIpAdapter(config, gateway);
        direct.Start();

        SteamBridgeAdapter? steamBridge = null;
        if (config.SteamEnabled)
        {
            steamBridge = new SteamBridgeAdapter(config, gateway);
            steamBridge.Start();
        }
        else
        {
            Log.Info("Steam 비활성화 (SteamEnabled=false).");
        }

        lobby.Push(force: true);

        while (!shutdownCts.IsCancellationRequested)
        {
            direct.PollEvents();
            steamBridge?.PollEvents();
            steamBridge?.Tick();
            gateway.Tick();
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(hub, sessions, instances, migrations, dataStore, banList, runRules, discordBot, lobby, modHandler, mapStore, objectBackgrounds, conn, msg);
                }
                catch (Exception ex)
                {
                    Log.Info($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            migrations.Tick();
            lobby.Push();

            instances.TickAgentCleanup();
            instances.TickSwap();
            instances.TickPrewarm();
            gateway.SetPrewarmGate(!instances.AdmissionReady);
            int online = TickRunLifecycle(sessions);
            instances.TickIdle(DateTime.UtcNow, key =>
                sessions.All.Count(s => s.InstanceId == key && s.Session != PlayerSessionState.Offline)
                + migrations.CountTargeting(key), online);
            console.Tick();
            Thread.Sleep(10);
        }

        Log.Info("종료 중 — 모드 데이터 저장 대기 후 스택 종료...");

        gateway.Shutdown();

        var mods = hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed).ToList();
        foreach (var conn in mods)
        {
            hub.SendNoAck(conn, MessageType.SHUTDOWN, null);
        }

        DateTime modDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < modDeadline && mods.Any(c => !c.Closed))
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(hub, sessions, instances, migrations, dataStore, banList, runRules, discordBot, lobby, modHandler, mapStore, objectBackgrounds, conn, msg);
                }
                catch (Exception ex)
                {
                    Log.Info($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            Thread.Sleep(20);
        }

        Log.Info("모드 종료 대기 완료 — 에이전트 종료 지시.");
        var agents = hub.Connections.Where(c => c.Kind == ClientKind.Agent && !c.Closed).ToList();
        foreach (var conn in agents)
        {
            hub.SendNoAck(conn, MessageType.SHUTDOWN, null);
        }

        DateTime agentDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < agentDeadline)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(hub, sessions, instances, migrations, dataStore, banList, runRules, discordBot, lobby, modHandler, mapStore, objectBackgrounds, conn, msg);
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
        direct.Stop();
        steamBridge?.Stop();
        steamApi?.Dispose();
        if (mapWeb != null)
        {
            try { mapWeb.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { mapWeb.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        hub.Stop();
        cts.Cancel();
    }

    private static int TickRunLifecycle(PlayerSessionStore sessions)
    {
        int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
        if (online > 0 && !_runActive)
        {
            _runActive = true;
        }
        else if (online == 0 && _runActive)
        {
            _runActive = false;
        }
        return online;
    }

    private static void Dispatch(ControlHub hub, PlayerSessionStore sessions,
        InstanceManager instances, MigrationCoordinator migrations, PlayerDataStore dataStore,
        BanList banList, RunRuleStore runRules, DiscordBot discordBot,
        LobbyMetadataService lobby, ModMessageHandler modHandler, WorldSnapshotStore mapStore,
        ObjectBackgroundStore objectBackgrounds, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.AGENT_HELLO:
                if (conn.Kind != ClientKind.Unknown) break;
                var agent = msg.PayloadAs<AgentHelloPayload>();
                if (agent == null) break;
                conn.Kind = ClientKind.Agent;
                conn.MachineId = agent.MachineId;
                conn.AgentCapacity = agent.Capacity;
                conn.AgentAddress = agent.Address;
                instances.OnAgentHello(conn, agent);
                hub.SendNoAck(conn, MessageType.AGENT_HELLO_ACK, new { ok = true });
                return;

            case MessageType.MOD_HELLO:
                if (conn.Kind != ClientKind.Unknown) break;
                var mod = msg.PayloadAs<ModHelloPayload>();
                if (mod == null) break;
                conn.Kind = ClientKind.Mod;
                conn.InstanceKey = mod.InstanceKey;
                conn.InstancePort = mod.Port;
                conn.InstanceDepth = mod.Depth;
                instances.OnModHello(conn, mod); // 인스턴스 연결 등록/READY 보고
                lobby.SetVersionTag(mod.VersionTag, mod.InstanceKey);
                mapStore.RegisterInstance(mod.InstanceKey, mod.Depth);
                hub.SendNoAck(conn, MessageType.MOD_HELLO_ACK, new { ok = true });
                hub.SendNoAck(conn, MessageType.RUN_RULES_STATE, // 규칙 초기 푸시
                    new { runSettings = runRules.RunSnapshot, rules = runRules.RuleSnapshot });
                return;
        }

        if (conn.Kind == ClientKind.Unknown)
        {
            return;
        }

        if (msg.Type == MessageType.LOG)
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
            case ClientKind.Agent:
                DispatchAgent(instances, sessions, mapStore, objectBackgrounds, conn, msg);
                break;
            case ClientKind.Mod:
                modHandler.Dispatch(conn, msg);
                break;
        }
    }

    private static void DispatchAgent(InstanceManager instances, PlayerSessionStore sessions,
        WorldSnapshotStore mapStore, ObjectBackgroundStore objectBackgrounds,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.INSTANCE_EXITED:
                if (msg.PayloadAs<InstanceExitedPayload>() is { } exited
                    && instances.OnInstanceExited(exited.InstanceKey, exited.Generation, exited.Code))
                {
                    sessions.OnInstanceExited(exited.InstanceKey);
                    mapStore.RemoveInstance(exited.InstanceKey);
                    objectBackgrounds.Remove(exited.InstanceKey);
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
            hub.SendNoAck(conn, MessageType.RUN_RULES_STATE, new { runSettings, rules });
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
