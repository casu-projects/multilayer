using System.Text.Json;
using System.Runtime.InteropServices;

namespace CasuMpOrchestrator;

internal static class Program
{
    private static bool _runActive;   // 런 수명주기: DORMANT(false) ↔ ACTIVE(true)

    private static void Main(string[] args)
    {
        TextWriter rawOut = Console.Out;
        ConsoleIO.Init(rawOut);
        Console.SetOut(new TimestampedConsoleWriter(rawOut));

        string configPath = args.Length > 0 ? args[0] : "orchestrator.json";
        OrchestratorConfig config = OrchestratorConfig.Load(configPath);

        Console.WriteLine($"구성 로드: {configPath}");
        Console.WriteLine($"제어 허브 포트: {config.Port}");

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

        hub.OnConnectionClosed += conn =>
        {
            switch (conn.Kind)
            {
                case ClientKind.Gateway:
                    Console.WriteLine("게이트웨이 연결 종료 — 재연결 대기.");
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

        var console = new OperatorConsole(hub, sessions, instances, migrations,
            (key, unban) =>
            {
                bool banned = unban != "unban";
                banList.Toggle(key, banned);
                hub.Send(hub.GatewayConnection, "BAN", new { playerKey = key, banned });
            },
            (key, reason) => hub.Send(hub.GatewayConnection, "KICK", new { playerKey = key, reason }),
            runRules,
            () =>
            {
                PushRunRulesToInstances(hub, runRules);
                PushLobbyMetadata(hub, runRules, sessions, force: true);
            },
            command =>
            {
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "CONSOLE", new { command });
                }
            },
            config: config, banList: banList, configPath: configPath);
        console.ShutdownRequested = () => shutdownCts.Cancel();
        console.Start();
        var discordBot = new DiscordBot(config.DiscordBotToken, config.DiscordChannelId,
            config.DiscordConsoleChannelId, config.SteamWebApiKey, config.DiscordAdminUserId,
            (username, text) =>
            {
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "CHAT", new { speaker = username, message = text, color = "", layer = "", prefix = "D", prefixColor = "#5865F2" });
                }
            },
            text =>
            {
                console.SubmitLine(text);
            });
        discordBot.OnReady = () =>
        {
            RefreshPlayerCountTopic(discordBot, sessions);
            _ = discordBot.SendServerStatusAsync(started: true);
        };
        console.LockdownNotify = on =>
            _ = on ? discordBot.SendLockdownAsync(started: true) : discordBot.SendLockdownAsync(started: false);
        TimestampedConsoleWriter.Instance!.OnConsoleLine = line => discordBot.EnqueueConsoleLog(line);

        migrations.MigrationCommitted += (player, fromDepth, toDepth, isRespawn) =>
        {
            var state = sessions.Get(player);
            string name = state?.Username ?? player.Value;
            ulong steamId = SteamIdOf(player);

            if (isRespawn)
            {
                _ = discordBot.SendDeathRespawnAsync(name, steamId, died: false, layer: "");

                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "ANNOUNCE", new
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
                PushLobbyMetadata(hub, runRules, sessions, force: true);

                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "ANNOUNCE", new
                    {
                        kind = "migration",
                        playerKey = player.Value,
                        name,
                        fromDepth,
                        toDepth,
                    });
                }
            }

            RefreshPlayerCountTopic(discordBot, sessions);
        };

        _ = discordBot.StartAsync();

        while (!shutdownCts.IsCancellationRequested)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, votes, discordBot, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            migrations.Tick();
            PushLobbyMetadata(hub, runRules, sessions);

            if (votes.TryFinalize(DateTime.UtcNow, out VoteCoordinator.VoteFinalizeResult voteResult))
            {
                FinalizeVote(hub, banList, voteResult);
            }
            instances.TickPrewarm();
            instances.TickIdle(DateTime.UtcNow, key =>
                sessions.All.Count(s => s.InstanceId == key && s.Session != PlayerSessionState.Offline)
                + migrations.CountTargeting(key));
            TickRunLifecycle(sessions);
            console.Tick();
            Thread.Sleep(20);
        }

        ConsoleIO.DisableInteractive();
        Console.WriteLine("종료 중 — 데이터 저장 후 스택 종료...");

        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }
        hub.SendNoAck(hub.GatewayConnection, "SHUTDOWN", null);
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Agent && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }

        DateTime shutdownDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < shutdownDeadline)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, votes, discordBot, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            Thread.Sleep(20);
        }

        sessions.PersistAllOffline(); // 재시작 시 스테일 온라인 복원 방지

        try { discordBot.SendServerStatusAsync(started: false).GetAwaiter().GetResult(); } catch { }

        Console.WriteLine("종료 완료.");
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
        BanList banList, RunRuleStore runRules, VoteCoordinator votes, DiscordBot discordBot,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "GATEWAY_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                conn.Kind = ClientKind.Gateway;
                conn.GatewayVersion = msg.PayloadAs<HelloVersion>()?.Version;
                sessions.PushTableSnapshot();
                hub.SendNoAck(conn, "AUTH_INFO", // 밴/서버 설정 푸시
                    new { serverName = config.ServerName, serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                hub.SendNoAck(conn, "ACK_REPLY", null);
                PushLobbyMetadata(hub, runRules, sessions, force: true);
                if (LockdownState.Active)
                {
                    hub.SendNoAck(conn, "MAINTENANCE", new
                    {
                        On = true,
                        Message = "Server is in maintenance mode",
                        KickAll = false,
                        Bypass = LockdownState.Bypass,
                    });
                    hub.SendNoAck(conn, "AUTH_INFO",
                        new { serverName = config.ServerName + " [MNT]", serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                }
                return;

            case "AGENT_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                var agent = msg.PayloadAs<AgentHelloPayload>();
                if (agent == null) break;
                conn.Kind = ClientKind.Agent;
                conn.MachineId = agent.MachineId;
                conn.AgentCapacity = agent.Capacity;
                conn.AgentAddress = agent.Address;
                instances.RegisterAgent(agent.MachineId, agent.Capacity, agent.Address);
                instances.PrewarmLayers(); // 레이어1 프리웜
                hub.SendNoAck(conn, "AGENT_HELLO_ACK", new { ok = true });
                return;

            case "MOD_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                var mod = msg.PayloadAs<ModHelloPayload>();
                if (mod == null) break;
                conn.Kind = ClientKind.Mod;
                conn.InstanceKey = mod.InstanceKey;
                conn.InstancePort = mod.Port;
                conn.InstanceDepth = mod.Depth;
                instances.OnModHello(conn, mod); // 인스턴스 연결 등록/READY 보고
                hub.SendNoAck(conn, "MOD_HELLO_ACK", new { ok = true });
                hub.SendNoAck(conn, "RUN_RULES_STATE", // 규칙 초기 푸시
                    new { runSettings = runRules.RunSnapshot, rules = runRules.RuleSnapshot });
                return;
        }

        if (conn.Kind == ClientKind.Unknown)
        {
            return;
        }

        if (msg.Type == "LOG")
        {
            LogPayload? log = msg.PayloadAs<LogPayload>();
            if (log != null && !string.IsNullOrEmpty(log.Message))
            {
                TimestampedConsoleWriter.Instance?.PrintRelayed(log.Source, log.Message);
            }
            return;
        }

        switch (conn.Kind)
        {
            case ClientKind.Gateway:
                DispatchGateway(config, hub, sessions, migrations, runRules, discordBot, conn, msg);
                break;
            case ClientKind.Agent:
                DispatchAgent(instances, sessions, conn, msg);
                break;
            case ClientKind.Mod:
                DispatchMod(hub, sessions, instances, migrations, dataStore, runRules, votes, discordBot, conn, msg);
                break;
        }
    }

    private static void DispatchGateway(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions, MigrationCoordinator migrations,
        RunRuleStore runRules, DiscordBot discordBot, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "SESSION_CONNECTED":
                if (TryPlayerKey(msg, out var connected))
                {
                    int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
                    if (online >= config.MaxPlayers)
                    {
                        Console.WriteLine($"인원 초과 — {connected} 접속 거부 (최대 {config.MaxPlayers}).");
                        hub.Send(hub.GatewayConnection, "KICK",
                            new { playerKey = connected.Value, reason = "Server is full." });
                        break;
                    }
                    sessions.OnSessionConnected(connected, GetUsername(msg));

                    _alive[connected] = true;
                    PushLobbyMetadata(hub, runRules, sessions, force: true);

                    string joinName = GetUsername(msg) ?? connected.Value;
                    foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(modConn, "ANNOUNCE", new
                        {
                            kind = "join",
                            playerKey = connected.Value,
                            name = joinName,
                            fromDepth = 0,
                            toDepth = 0,
                            originInstance = sessions.Get(connected)?.InstanceId ?? "",
                        });
                    }

                    _ = discordBot.SendJoinLeaveAsync(joinName,
                        SteamIdOf(connected), joined: true);
                    RefreshPlayerCountTopic(discordBot, sessions);
                }
                break;
            case "SESSION_DISCONNECTED":
                if (TryPlayerKey(msg, out var disc))
                {
                    if (migrations.IsMigrating(disc))
                    {
                        migrations.OnPlayerQuitDuringMigration(disc);
                    }
                    else
                    {
                        sessions.OnSessionDisconnected(disc);

                        _alive.Remove(disc);
                        PushLobbyMetadata(hub, runRules, sessions, force: true);
                    }

                    var discState = sessions.Get(disc);
                    string discName = discState?.Username ?? disc.Value;
                    _ = discordBot.SendJoinLeaveAsync(discName, SteamIdOf(disc), joined: false);
                    foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(modConn, "ANNOUNCE", new
                        {
                            kind = "leave",
                            playerKey = disc.Value,
                            name = discName,
                            fromDepth = 0,
                            toDepth = 0,
                            originInstance = discState?.InstanceId ?? "",
                        });
                    }

                    RefreshPlayerCountTopic(discordBot, sessions);
                }
                break;
            case "BACKEND_CONNECTED":
                if (TryPlayerKey(msg, out var bc) && msg.PayloadAs<InstancePayload>() is { } bcPayload)
                {
                    sessions.OnBackendConnected(bc, bcPayload.InstanceId);
                    migrations.OnBackendConnected(bc, bcPayload.InstanceId, GetEpoch(msg)); // 스왑 완료
                }
                break;
            case "SWAP_FAILED":
                if (TryPlayerKey(msg, out var sf) && msg.PayloadAs<SwapFailedPayload>() is { } sfPayload)
                    migrations.OnSwapFailed(sf, sfPayload.Reason ?? "unknown"); // 스왑 실패 롤백
                break;
            default:
                break;
        }
    }

    private static void DispatchAgent(InstanceManager instances, PlayerSessionStore sessions,
        ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "INSTANCE_EXITED":
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

    private static void DispatchMod(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, PlayerDataStore dataStore, RunRuleStore runRules, VoteCoordinator votes,
        DiscordBot discordBot, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "INSTANCE_READY":
                if (msg.PayloadAs<InstanceReadyPayload>() is { } ready
                    && instances.OnInstanceReady(ready.InstanceKey))
                {
                    sessions.PushRoutesForInstance(ready.InstanceKey);
                    migrations.OnInstanceReady(ready.InstanceKey); // READY -> 라우팅/SWAP
                }
                break;
            case "INSTANCE_FAULT":
                if (msg.PayloadAs<InstanceFaultPayload>() is { } fault)
                    instances.OnInstanceFault(fault.InstanceKey, fault.Reason ?? "unknown");
                break;
            case "LAYER_END":
                if (msg.PayloadAs<LayerEndPayload>() is { } layerEnd && TryPlayerKey(layerEnd.PlayerKey, out var le))
                    migrations.OnLayerEnd(le, layerEnd.FromDepth, layerEnd.MaxLayers); // 레이어 종료 -> 마이그레이션
                break;
            case "RESPAWN":
                if (msg.PayloadAs<RespawnPayload>() is { } respawn && TryPlayerKey(respawn.PlayerKey, out var rk))
                    migrations.OnRespawnRequest(rk, respawn.FromDepth); // !respawn
                break;
            case "FREEZE_DONE":
                if (TryPlayerKey(msg, out var fd))
                    migrations.OnFreezeDone(fd, GetEpoch(msg));
                break;
            case "PRECLEAR_DONE":
                if (TryPlayerKey(msg, out var pc))
                    migrations.OnPreClearDone(pc, GetEpoch(msg));
                break;
            case "RESUME_DONE":
                if (TryPlayerKey(msg, out var rd))
                    migrations.OnResumeDone(rd, GetEpoch(msg));
                break;
            case "WORLDGEN_DONE":
                if (TryPlayerKey(msg, out var wg))
                    migrations.OnWorldgenDone(wg, GetEpoch(msg));
                break;
            case "PLAYER_LEFT":
                if (TryPlayerKey(msg, out var pl))
                    migrations.OnPlayerLeftDuringMigration(pl, GetEpoch(msg));
                break;
            case "PLAYER_DATA_SUBMIT":
                if (TryPlayerKey(msg, out var ps) && msg.Payload is { } submitPayload
                    && submitPayload.TryGetProperty("payload", out JsonElement innerPayload))
                {
                    bool migration = migrations.IsMigrating(ps);
                    dataStore.OnSubmit(ps, innerPayload, migration); // 마이그레이션 중이면 보류
                }
                break;
            case "PLAYER_DATA_REQUEST":
                if (TryPlayerKey(msg, out var pr))
                    dataStore.OnRequest(pr, conn, hub, migrations.IsMigrating(pr));
                break;
            case "CHAT":
                if (msg.PayloadAs<ChatPayload>() is { } chat && !string.IsNullOrEmpty(chat.Speaker))
                {
                    string layerLabel = $"L{conn.InstanceDepth}";
                    int forwarded = 0;
                    foreach (ControlHub.ClientConnection other in hub.Connections
                        .Where(c => c.Kind == ClientKind.Mod && !c.Closed && c != conn))
                    {
                        hub.SendNoAck(other, "CHAT", new
                        {
                            speaker = chat.Speaker,
                            message = chat.Message,
                            color = chat.Color,
                            layer = layerLabel,
                        });
                        forwarded++;
                    }
                    if (forwarded > 0)
                    {
                    }

                    if (chat.Speaker != "*")
                    {
                        _ = discordBot.SendChatAsync(chat.Speaker, chat.Message,
                            layerLabel, chat.Color ?? "");
                    }
                }
                break;
            case "DIED":
            case "RESPAWNED":
                if (msg.PayloadAs<PlayerEventPayload>() is { } deathEvent)
                {
                    if (TryPlayerKey(deathEvent.PlayerKey, out var evKey))
                    {
                        _alive[evKey] = msg.Type == "RESPAWNED"; // 생존 상태 갱신
                        PushLobbyMetadata(hub, runRules, sessions, force: true);
                    }

                    bool migrating = TryPlayerKey(deathEvent.PlayerKey, out var evKey2)
                        && migrations.IsMigrating(evKey2);
                    if (!migrating)
                    {
                        _ = discordBot.SendDeathRespawnAsync(
                            deathEvent.Username ?? deathEvent.PlayerKey ?? "?",
                            TryPlayerKey(deathEvent.PlayerKey, out var evKey3) ? SteamIdOf(evKey3) : 0,
                            died: msg.Type == "DIED",
                            deathEvent.Layer ?? "");

                        if (msg.Type == "RESPAWNED")
                        {
                            string respawnName = deathEvent.Username ?? deathEvent.PlayerKey ?? "?";
                            foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                            {
                                hub.SendNoAck(modConn, "ANNOUNCE", new
                                {
                                    kind = "respawn",
                                    playerKey = deathEvent.PlayerKey,
                                    name = respawnName,
                                    fromDepth = 0,
                                    toDepth = 0,
                                });
                            }
                        }
                    }
                }
                break;
            case "LIST_REQUEST":
                if (msg.PayloadAs<ListRequestPayload>() is { } listReq)
                {
                    string[] lines = BuildPlayerListLines(sessions);
                    hub.SendNoAck(conn, "LIST_RESULT", new { playerKey = listReq.PlayerKey, lines });
                }
                break;
            case "CALLADMIN":
                if (msg.PayloadAs<CallAdminPayload>() is { } callAdmin)
                {
                    bool callOk = TryPlayerKey(callAdmin.PlayerKey, out var callKey);
                    _ = discordBot.SendCallAdminAsync(callAdmin.Username ?? callKey.Value ?? "?",
                        callOk ? SteamIdOf(callKey) : 0);
                }
                break;
            case "ANNOUNCE":
                if (msg.PayloadAs<AnnouncePayload>() is { } announce)
                {
                    foreach (var other in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(other, "ANNOUNCE", new
                        {
                            kind = announce.Kind,
                            playerKey = announce.PlayerKey,
                            name = announce.Name,
                            fromDepth = announce.FromDepth,
                            toDepth = announce.ToDepth,
                            originInstance = announce.OriginInstance,
                            text = announce.Text,
                        });
                    }
                }
                break;
            case "VOTE_START":
                if (msg.PayloadAs<VoteStartMarker>() is { } voteStart)
                {
                    HandleVoteStart(hub, sessions, votes, conn, voteStart);
                }
                break;
            case "VOTE_TALLY":
                if (msg.PayloadAs<VoteTallyMarker>() is { } tally)
                {
                    votes.RecordTally(tally, conn.InstanceKey ?? "");
                }
                break;
            default:
                break;
        }
    }

    private static bool TryPlayerKey(ControlMessage msg, out PlayerKey key)
    {
        key = default;
        string? value = msg.PayloadAs<PlayerKeyPayload>()?.PlayerKey;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    private static void HandleVoteStart(ControlHub hub, PlayerSessionStore sessions, VoteCoordinator votes,
        ControlHub.ClientConnection conn, VoteStartMarker marker)
    {
        string? callerClientId = marker.Payload.GetValueOrDefault("callerClientId");

        if (marker.Kind == "ban")
        {
            string targetQuery = marker.Payload.GetValueOrDefault("targetQuery", "");
            if (!TryResolveOnlinePlayer(sessions, targetQuery, out string resolvedKey, out string resolvedName))
            {
                RejectVote(hub, conn, marker, callerClientId,
                    $"Player not found: {targetQuery}");
                return;
            }
            marker.Payload["targetName"] = resolvedName;
            marker.Payload["targetKey"] = resolvedKey;
            marker.PromptBody = resolvedName;
        }

        var expected = hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed)
            .Select(c => c.InstanceKey ?? "")
            .Where(k => k.Length > 0)
            .ToList();

        if (!votes.TryStart(marker, expected))
        {
            RejectVote(hub, conn, marker, callerClientId,
                "A vote is already in progress. Please try again later.");
            return;
        }

        foreach (var other in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(other, "VOTE_RUN", new
            {
                voteId = marker.VoteId,
                kind = marker.Kind,
                title = marker.Title,
                promptBody = marker.PromptBody,
                timeoutSeconds = marker.TimeoutSeconds,
                payload = marker.Payload,
            });
        }
    }

    private static void RejectVote(ControlHub hub, ControlHub.ClientConnection conn,
        VoteStartMarker marker, string? callerClientId, string reason)
    {
        if (!string.IsNullOrEmpty(callerClientId))
        {
            hub.SendNoAck(conn, "VOTE_REJECTED", new { callerClientId, reason });
        }
    }

    private static bool TryResolveOnlinePlayer(PlayerSessionStore sessions, string query,
        out string playerKey, out string name)
    {
        playerKey = "";
        name = "";
        var match = sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .FirstOrDefault(s =>
                (s.Username != null && s.Username.Equals(query, StringComparison.OrdinalIgnoreCase))
                || s.Key.Value.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;

        playerKey = match.Key.Value;
        name = match.Username ?? match.Key.Value;
        return true;
    }

    private static void FinalizeVote(ControlHub hub, BanList banList,
        VoteCoordinator.VoteFinalizeResult voteResult)
    {
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, "VOTE_RESULT", new
            {
                voteId = voteResult.VoteId,
                kind = voteResult.Kind,
                yes = voteResult.Yes,
                no = voteResult.No,
                ignore = voteResult.Ignore,
                payload = voteResult.Payload,
            });
        }

        int totalVotes = voteResult.Yes + voteResult.No + voteResult.Ignore;
        bool votePassed = totalVotes > 0 && (float)voteResult.Yes / totalVotes > 0.5f;

        if (!votePassed) return;

        if (voteResult.Kind == "ban")
        {
            string? targetKey = voteResult.Payload.GetValueOrDefault("targetKey");
            if (!string.IsNullOrEmpty(targetKey))
            {
                banList.Toggle(targetKey, true);
                hub.Send(hub.GatewayConnection, "BAN", new { playerKey = targetKey, banned = true });
                Console.WriteLine($"[Vote] 투표 가결 — {targetKey} 밴 처리 (파일 + 게이트웨이 차단/킥).");
            }
        }
    }

    private static void PushRunRulesToInstances(ControlHub hub, RunRuleStore runRules)
    {
        var runSettings = runRules.RunSnapshot;
        var rules = runRules.RuleSnapshot;
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, "RUN_RULES_STATE", new { runSettings, rules });
        }
    }

    private static bool TryPlayerKey(string? value, out PlayerKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value)) return false;
        key = PlayerKey.FromString(value);
        return true;
    }

    private static int? GetEpoch(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("epoch", out JsonElement e) && e.TryGetInt32(out int v) ? v : null;
    }

    private static string? GetUsername(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("username", out JsonElement u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }

    private static string[] BuildPlayerListLines(PlayerSessionStore sessions)
    {
        var byDepth = sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .GroupBy(s => s.Depth)
            .OrderBy(g => g.Key)
            .ToList();
        if (byDepth.Count == 0) return new[] { "No players online." };

        return byDepth.Select(g =>
            $"[L{g.Key}] {string.Join(", ", g.Select(s => s.Username ?? s.Key.Value))}").ToArray();
    }

    private static string BuildPlayerCountText(PlayerSessionStore sessions)
    {
        var online = sessions.All.Where(s => s.Session != PlayerSessionState.Offline).ToList();
        var parts = new List<string> { $"{online.Count} online" };
        foreach (var g in online.GroupBy(s => s.Depth).OrderBy(g => g.Key))
        {
            parts.Add($"[L{g.Key}] {g.Count()}");
        }
        return string.Join(" / ", parts);
    }

    private static void RefreshPlayerCountTopic(DiscordBot discordBot, PlayerSessionStore sessions)
    {
        _ = discordBot.UpdatePlayerCountTopicAsync(BuildPlayerCountText(sessions));
    }

    private sealed class HelloVersion { public int Version { get; set; } }
    private sealed class AgentHelloPayload { public string MachineId { get; set; } = ""; public int Capacity { get; set; } public string Address { get; set; } = ""; }
    private sealed class InstanceExitedPayload { public string InstanceKey { get; set; } = ""; public int Code { get; set; } }
    private sealed class InstanceReadyPayload { public string InstanceKey { get; set; } = ""; }
    private sealed class InstanceFaultPayload { public string InstanceKey { get; set; } = ""; public string? Reason { get; set; } }
    private sealed class LayerEndPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } public int MaxLayers { get; set; } }

    private sealed class RespawnPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } }

    private sealed class LogPayload { public string Source { get; set; } = ""; public string Message { get; set; } = ""; }
    private sealed class PlayerKeyPayload { public string? PlayerKey { get; set; } }
    private sealed class InstancePayload { public string InstanceId { get; set; } = ""; }
    private sealed class SwapFailedPayload { public string? Reason { get; set; } }

    private sealed class ChatPayload
    {
        public string Speaker { get; set; } = "";
        public string Message { get; set; } = "";
        public string Color { get; set; } = "";
    }

    private sealed class ListRequestPayload
    {
        public string PlayerKey { get; set; } = "";
    }

    private sealed class CallAdminPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Username { get; set; } = "";
    }

    private sealed class PlayerEventPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Username { get; set; } = "";
        public string Layer { get; set; } = "";
    }

    private sealed class AnnouncePayload
    {
        public string Kind { get; set; } = "";
        public string PlayerKey { get; set; } = "";
        public string Name { get; set; } = "";
        public int FromDepth { get; set; }
        public int ToDepth { get; set; }
        public string OriginInstance { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private static ulong SteamIdOf(PlayerKey key) =>
        key.Value.StartsWith("STEAM_") && ulong.TryParse(key.Value.AsSpan(6), out ulong sid) ? sid : 0;

    private static readonly Dictionary<PlayerKey, bool> _alive = new();

    private static DateTime _lastLobbyMetadataPush = DateTime.MinValue;
    private static readonly TimeSpan LobbyMetadataInterval = TimeSpan.FromSeconds(30);

    private static void PushLobbyMetadata(ControlHub hub, RunRuleStore runRules,
        PlayerSessionStore sessions, bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastLobbyMetadataPush < LobbyMetadataInterval) return;
        _lastLobbyMetadataPush = now;

        var gateway = hub.GatewayConnection;
        if (gateway == null) return;

        var online = sessions.All.Where(s => s.Session != PlayerSessionState.Offline).ToList();
        ulong[] steamIds = online
            .Select(s => SteamIdOf(s.Key))
            .Where(id => id != 0)
            .ToArray();
        int livingCount = online.Count(s => _alive.GetValueOrDefault(s.Key, true));

        string rulesBase64 = Convert.ToBase64String(RulesBlobBuilder.Build(runRules.RuleSnapshot));
        hub.SendNoAck(gateway, "LOBBY_METADATA", new
        {
            livingCount,
            happinessSum = 0,
            steamIds,
            rulesBase64,
        });
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
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_banned.ToArray())); }
        catch { }
    }
}
