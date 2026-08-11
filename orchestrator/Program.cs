using System.Text.Json;
using System.Runtime.InteropServices;

namespace CasuMpOrchestrator;

internal static class Program
{
    private static bool _runActive;   // 런 수명주기: DORMANT(false) ↔ ACTIVE(true)

    private static void Main(string[] args)
    {
        // 전역 출력: ConsoleIO(대화형 — 입력줄 보호 + ↑↓ 히스토리) + 타임스탬프 래퍼.
        // rawOut은 SetOut 전에 캡처 — ConsoleIO가 래퍼를 우회해 입력줄/ANSI를 즉시 반영.
        TextWriter rawOut = Console.Out;
        ConsoleIO.Init(rawOut);
        Console.SetOut(new TimestampedConsoleWriter(rawOut));

        string configPath = args.Length > 0 ? args[0] : "orchestrator.json";
        OrchestratorConfig config = OrchestratorConfig.Load(configPath);
        VerboseState.Active = config.Verbose;

        Console.WriteLine($"구성 로드: {configPath}");
        Console.WriteLine($"제어 허브 포트: {config.Port}");

        // 허브 수명 토큰(cts)과 종료 신호(shutdownCts) 분리 — graceful 종료 중에도 허브의
        // 연결이 유지되어야 SHUTDOWN 브로드캐스트가 전달된다. cts를 종료 신호에 직접
        // 사용하면 ControlHub의 per-connection CTS가 링크되어 연결이 먼저 끊겨 전파가 불가능.
        using var cts = new CancellationTokenSource();
        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        // SIGTERM (kill/데몬) — graceful 종료 캐스케이드. 대화형 Ctrl+C는 ReadKey가 SIGINT를
        // 키로 소비하므로 OperatorConsole.ShutdownRequested(아래)가 담당한다.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

        var hub = new ControlHub(config, cts.Token);
        var runRules = new RunRuleStore(config.RunTablePath, config.RuleTablePath);
        var instances = new InstanceManager(config, hub);
        var sessions = new PlayerSessionStore(config, hub, instances);
        var dataStore = new PlayerDataStore(config);
        var migrations = new MigrationCoordinator(config, hub, instances, sessions, dataStore);
        var groups = new GroupStore(config);

        var banList = new BanList(config.BanListPath);

        // 크로스 인스턴스 투표 조정 (VOTE_START → VOTE_RUN 릴레이 → tally 합산 → VOTE_RESULT).
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
                // 게이트웨이에 즉시 전파 — 접속 중이면 킥 (O6-8 전파 구현)
                hub.Send(hub.GatewayConnection, "BAN", new { playerKey = key, banned });
            },
            (key, reason) => hub.Send(hub.GatewayConnection, "KICK", new { playerKey = key, reason }),
            runRules,
            () =>
            {
                // rule/run 변경 → 전 인스턴스 즉시 반영 (모드가 재적용).
                PushRunRulesToInstances(hub, runRules);
                // G13 — rule/run 변경 시 로비 rulesblob 즉시 반영.
                PushLobbyMetadata(hub, runRules, sessions, force: true);
            },
            command =>
            {
                // 모드의 CONSOLE 핸들러(RunModule.HandleConsole)가 실행하고, 실행 결과는
                // 인스턴스 로그(에이전트 DrainAsync)로 자연히 표시된다 — 여기서는 전송만.
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "CONSOLE", new { command });
                }
            },
            config: config, banList: banList, configPath: configPath);
        // 대화형 Ctrl+C → graceful 종료 (ReadKey가 SIGINT를 소비하므로 키 감지로 연결)
        console.ShutdownRequested = () => shutdownCts.Cancel();
        console.Start();
        // Discord 관리 봇 (D1) — 토큰/채널 미설정 시 비활성.
        // 채널 주제에 접속 인원을 실시간 표시 (Ready 초기 동기화 + 접속/퇴장/마이그레이션 갱신).
        var discordBot = new DiscordBot(config.DiscordBotToken, config.DiscordChannelId,
            config.DiscordConsoleChannelId, config.SteamWebApiKey, config.DiscordAdminUserId,
            (username, text) =>
            {
                // Discord 채팅 → 게임 — 전 인스턴스에 "[D] [유저명]" 스피커로 브로드캐스트.
                // 모드 ChatRelay는 플레이어 채팅만 오케스트레이터로 올리므로 루프 없음.
                foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                {
                    hub.SendNoAck(conn, "CHAT", new { speaker = username, message = text, color = "", layer = "", prefix = "D", prefixColor = "#5865F2" });
                }
            },
            text =>
            {
                // Discord 콘솔 채널 → 서버 콘솔 명령 — 터미널 입력과 동일 경로 (SubmitLine).
                console.SubmitLine(text);
            });
        // Ready 시 채널 주제 초기 동기화 (Ready는 StartAsync 이후 비동기 발화 — 안전).
        discordBot.OnReady = () =>
        {
            RefreshPlayerCountTopic(discordBot, sessions);
            // 서버 시작 알림 (메시지 채널).
            _ = discordBot.SendServerStatusAsync(started: true);
        };
        // 락다운 시작/종료 → Discord 알림 (메시지 채널).
        console.LockdownNotify = on =>
            _ = on ? discordBot.SendLockdownAsync(started: true) : discordBot.SendLockdownAsync(started: false);
        // 그룹 스레드 채팅 → 게임 그룹 멤버 라우팅. username은 무색 (D 배지만 파랑).
        discordBot.OnThreadChat = (username, message, threadId) =>
        {
            if (groups.TryGetByThreadId(threadId, out Group? group))
            {
                RouteGroupChat(hub, sessions, groups, discordBot, group, senderKey: null,
                    username, message, color: "", layerLabel: "");
            }
        };
        // 전체 콘솔 로그 → Discord 콘솔 채널 (접두사 포함 — 오케스트레이터 + 릴레이 전부).
        TimestampedConsoleWriter.Instance!.OnConsoleLine = line => discordBot.EnqueueConsoleLog(line);

        // 마이그레이션 커밋 → Discord 알림 (L{from} > L{to}) + 로비 메타데이터 갱신 + 인게임 공지.
        migrations.MigrationCommitted += (player, fromDepth, toDepth) =>
        {
            var state = sessions.Get(player);
            _ = discordBot.SendMigrationAsync(state?.Username ?? player.Value, SteamIdOf(player),
                fromDepth, toDepth);
            PushLobbyMetadata(hub, runRules, sessions, force: true);

            // 인게임 공지 — 본인 제외 전 인스턴스 ({이름}님이 L{from}에서 L{to}로 이동합니다).
            string name = state?.Username ?? player.Value;
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

            // D1 — 레이어 이동으로 인한 레이어별 인원 변동 — 채널 주제 갱신.
            RefreshPlayerCountTopic(discordBot, sessions);
        };

        // D1 — Discord 봇 시작 (토큰/채널 미설정 시 내부 no-op).
        _ = discordBot.StartAsync();

        // 메인 루프 — 모든 상태 접근은 이 스레드에서만.
        while (!shutdownCts.IsCancellationRequested)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, votes, discordBot, groups, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            migrations.Tick();
            // Steam 로비 메타데이터 주기 갱신 (8초 스로틀 — 세션 변동 시 force 푸시가 즉시 반영).
            PushLobbyMetadata(hub, runRules, sessions);

            // 투표 확정 — 전 인스턴스 VOTE_RESULT + Discord + 가결 시 효과 적용 (밴/런).
            if (votes.TryFinalize(DateTime.UtcNow, out VoteCoordinator.VoteFinalizeResult voteResult))
            {
                FinalizeVote(hub, sessions, runRules, banList, discordBot, voteResult);
            }
            // 유휴 정리 계수: 마이그레이션 중인 플레이어(Migrating, InstanceId=출발지)와
            // 도착 대기 중인 목적지(tx.TargetInstance)도 인원으로 계산 — FREEZE/READY 대기 중
            // 출발지·목적지가 유휴 오판정으로 강제 정지되는 것을 방지한다.
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

        // SHUTDOWN 브로드캐스트 — 모드들(데이터 제출+quit) / 게이트웨이(전 세션 Kick+종료) /
        // 에이전트(인스턴스 graceful 정리+종료).
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }
        hub.SendNoAck(hub.GatewayConnection, "SHUTDOWN", null);
        foreach (var conn in hub.Connections.Where(c => c.Kind == ClientKind.Agent && !c.Closed))
        {
            hub.SendNoAck(conn, "SHUTDOWN", null);
        }

        // 유예 창: 메인 루프를 계속 돌리며 플레이어 데이터 제출(PLAYER_DATA_SUBMIT)을
        // 수신·디스크 저장한다 (모드가 제출 → 오케스트레이터가 영속화 → 재시작 시 복원).
        DateTime shutdownDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < shutdownDeadline)
        {
            hub.Tick();
            hub.DrainInbound((conn, msg) =>
            {
                try
                {
                    Dispatch(config, hub, sessions, instances, migrations, dataStore, banList, runRules, votes, discordBot, groups, conn, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"메시지 처리 실패 ({msg.Type}): {ex.Message}");
                }
            });
            Thread.Sleep(20);
        }

        // 세션 전부 Offline 영속화 — 재시작 시 클린 상태 (스테일 온라인 복원 방지).
        sessions.PersistAllOffline();

        // 서버 종료 알림 (메시지 채널) — 프로세스 종료 전 전송 완료 대기.
        try { discordBot.SendServerStatusAsync(started: false).GetAwaiter().GetResult(); } catch { }

        Console.WriteLine("종료 완료.");
        try { discordBot.StopAsync().GetAwaiter().GetResult(); } catch { }
        hub.Stop();
        // 허브 백그라운드 태스크(리스너/연결 서비스) 정리 — 프로세스가 곧 종료되지만 명시적 해제.
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
        GroupStore groups, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "GATEWAY_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                conn.Kind = ClientKind.Gateway;
                conn.GatewayVersion = msg.PayloadAs<HelloVersion>()?.Version;
                VerboseState.Line($"게이트웨이 등록 (version {conn.GatewayVersion}).");
                sessions.PushTableSnapshot();
                // 인증/밴 정보 푸시 — 밴 목록 단일 소유자는 오케스트레이터, 게이트웨이는
                // 메모리 사본으로 접속 시 검증 (O6-8 — 재연결 시 스냅샷 재푸시로 수렴)
                hub.SendNoAck(conn, "AUTH_INFO",
                    new { serverName = config.ServerName, serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
                hub.SendNoAck(conn, "ACK_REPLY", null);
                // 새 게이트웨이 — Steam 로비 메타데이터 즉시 푸시 (rules/players).
                PushLobbyMetadata(hub, runRules, sessions, force: true);
                // 디버그 로그 표시 상태 전파.
                hub.SendNoAck(conn, "VERBOSE", new { on = VerboseState.Active });
                // 락다운 활성 상태면 재푸시 (게이트웨이 재시작에도 유지 — 상태는 메모리 전용).
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
                        new { serverName = config.ServerName + " (MAINTENANCE)", serverPassword = config.ServerPassword, bannedKeys = banList.All, maxPlayers = config.MaxPlayers });
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
                // 프리웜 — PrewarmLayer 스폰 (수용량 내 최대한, 기존 인스턴스 재사용).
                // 에이전트가 추가될 때마다 재실행되어 누락 레이어가 자동 보충된다.
                instances.PrewarmLayers();
                hub.SendNoAck(conn, "AGENT_HELLO_ACK", new { ok = true });
                // 디버그 로그 표시 상태 전파.
                hub.SendNoAck(conn, "VERBOSE", new { on = VerboseState.Active });
                return;

            case "MOD_HELLO":
                if (conn.Kind != ClientKind.Unknown) break;
                var mod = msg.PayloadAs<ModHelloPayload>();
                if (mod == null) break;
                conn.Kind = ClientKind.Mod;
                conn.InstanceKey = mod.InstanceKey;
                conn.InstancePort = mod.Port;
                conn.InstanceDepth = mod.Depth;
                instances.OnModHello(conn, mod);
                hub.SendNoAck(conn, "MOD_HELLO_ACK", new { ok = true });
                hub.SendNoAck(conn, "RUN_RULES_STATE",
                    new { runSettings = runRules.RunSnapshot, rules = runRules.RuleSnapshot });
                // 디버그 로그 표시 상태 전파.
                hub.SendNoAck(conn, "VERBOSE", new { on = VerboseState.Active });
                return;
        }

        if (conn.Kind == ClientKind.Unknown)
        {
            return;
        }

        // 실시간 로그 릴레이 (agent/gateway → orchestrator) — 타임스탬프와 [source] 접두사는
        // 표시 계층(TimestampedConsoleWriter.PrintRelayed)이 부여한다 (메시지는 클린 상태).
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
                DispatchGateway(config, hub, sessions, migrations, runRules, discordBot, groups, conn, msg);
                break;
            case ClientKind.Agent:
                DispatchAgent(instances, sessions, conn, msg);
                break;
            case ClientKind.Mod:
                DispatchMod(hub, sessions, instances, migrations, dataStore, runRules, votes, discordBot, groups, config.DiscordUrl, conn, msg);
                break;
        }
    }

    private static void DispatchGateway(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions, MigrationCoordinator migrations,
        RunRuleStore runRules, DiscordBot discordBot, GroupStore groups, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "SESSION_CONNECTED":
                if (TryPlayerKey(msg, out var connected))
                {
                    // 인원 제한 2차 검증 (1차는 게이트웨이 접속 단계 — 여기는 게이트웨이
                    // 카운트가 낡은 경우의 안전망). 초과 시 게이트웨이에 KICK.
                    int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
                    if (online >= config.MaxPlayers)
                    {
                        Console.WriteLine($"인원 초과 — {connected} 접속 거부 (최대 {config.MaxPlayers}).");
                        hub.Send(hub.GatewayConnection, "KICK",
                            new { playerKey = connected.Value, reason = "Server is full." });
                        break;
                    }
                    sessions.OnSessionConnected(connected, GetUsername(msg));

                    // 스테일 그룹 채팅 모드 정리 — 그룹 미가입자는 Global이 기본 (오프라인 중
                    // 그룹 이탈/삭제 시 모드 dict에 남은 Group 잔존 방지).
                    if (groups.GetByPlayer(connected) == null)
                    {
                        SendChatModeReset(hub, sessions, connected.Value);
                    }

                    // G13 — 접속 시 생존 상태 기본값(생존) + 로비 목록 즉시 갱신.
                    _alive[connected] = true;
                    PushLobbyMetadata(hub, runRules, sessions, force: true);

                    // D1 — Discord 접속 알림.
                    _ = discordBot.SendJoinLeaveAsync(GetUsername(msg) ?? connected.Value,
                        SteamIdOf(connected), joined: true);
                    RefreshPlayerCountTopic(discordBot, sessions);
                }
                break;
            case "SESSION_DISCONNECTED":
                if (TryPlayerKey(msg, out var disc))
                {
                    if (migrations.IsMigrating(disc))
                    {
                        // 마이그레이션 중 실퇴장 — 데이터 확정 + Depth 증가 (S7)
                        migrations.OnPlayerQuitDuringMigration(disc);
                    }
                    else
                    {
                        sessions.OnSessionDisconnected(disc);

                        // G13 — 생존 상태 해제 + 로비 목록 즉시 갱신.
                        _alive.Remove(disc);
                        PushLobbyMetadata(hub, runRules, sessions, force: true);

                        // D1 — Discord 퇴장 알림 (마이그레이션 중 퇴장은 마이그레이션 알림이
                        // 별도로 발행되므로 중복 방지).
                        var discState = sessions.Get(disc);
                        _ = discordBot.SendJoinLeaveAsync(discState?.Username ?? disc.Value,
                            SteamIdOf(disc), joined: false);
                    }

                    // D1 — 접속 인원 변동 (마이그레이션 중 퇴장 분기 포함) — 채널 주제 갱신.
                    RefreshPlayerCountTopic(discordBot, sessions);
                }
                break;
            case "BACKEND_CONNECTED":
                if (TryPlayerKey(msg, out var bc) && msg.PayloadAs<InstancePayload>() is { } bcPayload)
                {
                    sessions.OnBackendConnected(bc, bcPayload.InstanceId);
                    migrations.OnBackendConnected(bc, bcPayload.InstanceId, GetEpoch(msg));
                }
                break;
            case "SWAP_FAILED":
                if (TryPlayerKey(msg, out var sf) && msg.PayloadAs<SwapFailedPayload>() is { } sfPayload)
                    migrations.OnSwapFailed(sf, sfPayload.Reason ?? "unknown");
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
                    // 정지/크래시 창에 배정 대기 중이던 세션 재배정 (Stopping 엣지 복구)
                    sessions.OnInstanceExited(exited.InstanceKey);
                }
                break;
            default:
                break;
        }
    }

    private static void DispatchMod(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, PlayerDataStore dataStore, RunRuleStore runRules, VoteCoordinator votes,
        DiscordBot discordBot, GroupStore groups, string discordUrl, ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case "INSTANCE_READY":
                if (msg.PayloadAs<InstanceReadyPayload>() is { } ready
                    && instances.OnInstanceReady(ready.InstanceKey))
                {
                    // ROUTE-ON-READY: 실제 READY 전이 시에만 대기 세션 라우팅 + WaitingReady
                    // 마이그레이션 SWAP 발행 (중복 READY 보고는 무시 — 푸시 1회 보장).
                    sessions.PushRoutesForInstance(ready.InstanceKey);
                    migrations.OnInstanceReady(ready.InstanceKey);
                }
                break;
            case "INSTANCE_FAULT":
                if (msg.PayloadAs<InstanceFaultPayload>() is { } fault)
                    instances.OnInstanceFault(fault.InstanceKey, fault.Reason ?? "unknown");
                break;
            case "LAYER_END":
                if (msg.PayloadAs<LayerEndPayload>() is { } layerEnd && TryPlayerKey(layerEnd.PlayerKey, out var le))
                    migrations.OnLayerEnd(le, layerEnd.FromDepth, layerEnd.MaxLayers);
                break;
            case "RESPAWN":
                if (msg.PayloadAs<RespawnPayload>() is { } respawn && TryPlayerKey(respawn.PlayerKey, out var rk))
                    migrations.OnRespawnRequest(rk, respawn.FromDepth);
                break;
            case "FREEZE_DONE":
                if (TryPlayerKey(msg, out var fd))
                    migrations.OnFreezeDone(fd, GetEpoch(msg));
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
                    dataStore.OnSubmit(ps, innerPayload, migration);
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

                    // 그룹 채팅 — 그룹 멤버가 있는 인스턴스에만 targets(멤버 이름)와 함께 전송.
                    // 발신자 본인은 모드가 senderKey로 제외한다 (같은 인스턴스의 다른 멤버는 수신).
                    if (chat.Mode == "group")
                    {
                        if (TryPlayerKey(chat.PlayerKey, out var senderKey))
                        {
                            Group? group = groups.GetByPlayer(senderKey);
                            if (group == null)
                            {
                                // 그룹 미가입 — 안내 (그룹 모드인데 그룹이 없음).
                                SendGroupResult(hub, sessions, chat.PlayerKey,
                                    new[] { "그룹에 가입되어 있지 않습니다. !group list로 확인하세요." });
                            }
                            else
                            {
                                RouteGroupChat(hub, sessions, groups, discordBot, group,
                                    senderKey: senderKey.Value,
                                    chat.Speaker, chat.Message, chat.Color ?? "", layerLabel);
                            }
                        }
                        break;
                    }

                    // 크로스 인스턴스 채팅 릴레이: 발신자 제외 전 인스턴스에 레이어 태그 부여 재전송.
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

                    // D1 — 게임 채팅 → Discord (시스템 공지 "*" 제외 — 플레이어 메시지만).
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
                    // 생존 상태 추적 — 사망/리스폰 즉시 로비 생존자 수 반영 (G13).
                    if (TryPlayerKey(deathEvent.PlayerKey, out var evKey))
                    {
                        _alive[evKey] = msg.Type == "RESPAWNED";
                        PushLobbyMetadata(hub, runRules, sessions, force: true);
                    }

                    // 마이그레이션 중 사망/리스폰은 마이그레이션 알림과 중복 — Discord는 스킵.
                    bool migrating = TryPlayerKey(deathEvent.PlayerKey, out var evKey2)
                        && migrations.IsMigrating(evKey2);
                    if (!migrating)
                    {
                        _ = discordBot.SendDeathRespawnAsync(
                            deathEvent.Username ?? deathEvent.PlayerKey ?? "?",
                            TryPlayerKey(deathEvent.PlayerKey, out var evKey3) ? SteamIdOf(evKey3) : 0,
                            died: msg.Type == "DIED",
                            deathEvent.Layer ?? "");
                    }
                }
                break;
            case "LIST_REQUEST":
                // !list — 전 세션 depth별 집계 응답 (라인 배열 — 모드가 라인별 개인 회신).
                if (msg.PayloadAs<ListRequestPayload>() is { } listReq)
                {
                    string[] lines = BuildPlayerListLines(sessions);
                    hub.SendNoAck(conn, "LIST_RESULT", new { playerKey = listReq.PlayerKey, lines });
                }
                break;
            case "CURRENT_REQUEST":
                // !currentrun [key] — RunRuleStore 단일 소유자 조회.
                if (msg.PayloadAs<CurrentRequestPayload>() is { } curReq)
                {
                    string text = BuildRunSettingsText(runRules, curReq.Key);
                    hub.SendNoAck(conn, "CURRENT_RESULT", new { playerKey = curReq.PlayerKey, text });
                }
                break;
            case "DISCORD_REQUEST":
                // !discord — 설정된 디스코드 서버 초대 URL 회신 (모드가 개인 채팅 2줄로 표시).
                if (msg.PayloadAs<DiscordRequestPayload>() is { } discReq)
                {
                    hub.SendNoAck(conn, "DISCORD_RESULT", new { playerKey = discReq.PlayerKey, url = discordUrl });
                }
                break;
            case "CALLADMIN":
                if (msg.PayloadAs<CallAdminPayload>() is { } callAdmin)
                {
                    // D1 — Discord 관리자 호출 알림 (+ 멘션).
                    bool callOk = TryPlayerKey(callAdmin.PlayerKey, out var callKey);
                    _ = discordBot.SendCallAdminAsync(callAdmin.Username ?? callKey.Value ?? "?",
                        callOk ? SteamIdOf(callKey) : 0);
                }
                break;
            case "ANNOUNCE":
                // 크로스 인스턴스 시스템 공지 — 발신 포함 전 인스턴스 에코 (사망 공지 등).
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
                        });
                    }
                }
                break;
            case "GROUP_REQUEST":
                if (msg.PayloadAs<GroupRequestPayload>() is { } groupReq
                    && TryPlayerKey(groupReq.PlayerKey, out var groupPlayer))
                {
                    HandleGroupRequest(hub, sessions, instances, groups, discordBot, conn, groupPlayer, groupReq);
                }
                break;
            case "GROUP_INVITE_RESULT":
                if (msg.PayloadAs<GroupInviteResultPayload>() is { } inviteResult
                    && TryPlayerKey(inviteResult.PlayerKey, out var invitePlayer))
                {
                    HandleGroupInviteResult(hub, sessions, groups, discordBot, invitePlayer, inviteResult);
                }
                break;
            case "CHATMODE_REQUEST":
                if (msg.PayloadAs<ChatModeRequestPayload>() is { } cmReq && TryPlayerKey(cmReq.PlayerKey, out var cmPlayer))
                {
                    // 그룹 모드 진입 검증 — 미가입이면 모드가 Global로 건너뛴다.
                    bool ok = cmReq.Mode == "group" && groups.GetByPlayer(cmPlayer) != null;
                    hub.SendNoAck(conn, "CHATMODE_RESULT", new { playerKey = cmReq.PlayerKey, ok });
                }
                break;
            case "VOTE_START":
                if (msg.PayloadAs<VoteStartMarker>() is { } voteStart)
                {
                    HandleVoteStart(hub, sessions, votes, discordBot, conn, voteStart);
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


    // VOTE_START 수신 — ban 대상 해석(온라인 세션) + 활성 투표 검증 후 전 인스턴스
    // VOTE_RUN 브로드캐스트 + Discord 공지. 거부 시 VOTE_REJECTED로 발신자 개인 회신.
    private static void HandleVoteStart(ControlHub hub, PlayerSessionStore sessions, VoteCoordinator votes,
        DiscordBot discordBot, ControlHub.ClientConnection conn, VoteStartMarker marker)
    {
        string? callerClientId = marker.Payload.GetValueOrDefault("callerClientId");

        // ban 대상 해석 — 온라인 세션만 (이름 또는 PlayerKey).
        if (marker.Kind == "ban")
        {
            string targetQuery = marker.Payload.GetValueOrDefault("targetQuery", "");
            if (!TryResolveOnlinePlayer(sessions, targetQuery, out string resolvedKey, out string resolvedName))
            {
                RejectVote(hub, discordBot, conn, marker, callerClientId,
                    $"플레이어를 찾을 수 없습니다: {targetQuery}");
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
            RejectVote(hub, discordBot, conn, marker, callerClientId,
                "이미 진행 중인 투표가 있습니다. 잠시 후 다시 시도해주세요.");
            return;
        }

        // 전 인스턴스 VOTE_RUN (발신 포함 — 모든 레이어가 투표).
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

        _ = discordBot.SendVoteStartAsync(marker.Kind, marker.Title, marker.PromptBody, marker.TimeoutSeconds);
    }

    private static void RejectVote(ControlHub hub, DiscordBot discordBot, ControlHub.ClientConnection conn,
        VoteStartMarker marker, string? callerClientId, string reason)
    {
        if (!string.IsNullOrEmpty(callerClientId))
        {
            hub.SendNoAck(conn, "VOTE_REJECTED", new { callerClientId, reason });
        }
        _ = discordBot.SendVoteRejectedAsync(marker.Kind, marker.Title, reason);
    }

    // 온라인 세션에서 대상 해석 — 이름(대소문자 무시) 또는 PlayerKey 일치.
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

    // 투표 확정 처리 — VOTE_RESULT 브로드캐스트 + Discord 결과 + 가결 시 효과:
    // ban → 밴 파일 + 게이트웨이 즉시 차단/킥, run → run.json 즉시 반영 (RUN_RULES_STATE 푸시).
    private static void FinalizeVote(ControlHub hub, PlayerSessionStore sessions, RunRuleStore runRules,
        BanList banList, DiscordBot discordBot, VoteCoordinator.VoteFinalizeResult voteResult)
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
        string? title = voteResult.Payload.GetValueOrDefault("key")
            ?? voteResult.Payload.GetValueOrDefault("targetName")
            ?? "";
        _ = discordBot.SendVoteResultAsync(voteResult.Kind, title,
            voteResult.Yes, voteResult.No, voteResult.Ignore, votePassed, null);

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
        else if (voteResult.Kind == "run")
        {
            string? key = voteResult.Payload.GetValueOrDefault("key");
            string? rawValue = voteResult.Payload.GetValueOrDefault("rawValue");
            if (!string.IsNullOrEmpty(key) && rawValue != null)
            {
                runRules.SetRun(key, rawValue);   // run.json 재기록 (정본)
                PushRunRulesToInstances(hub, runRules);
                PushLobbyMetadata(hub, runRules, sessions, force: true);
                Console.WriteLine($"[Vote] 투표 가결 — {key} = {rawValue} 즉시 적용 (전 인스턴스).");
            }
        }
    }

    // RUN_RULES_STATE 전 인스턴스 브로드캐스트 (rule/run 명령 푸시와 동일 경로).
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

    // 마이그레이션 이벤트 payload의 epoch 추출 (없으면 null — 하위 호환).
    private static int? GetEpoch(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("epoch", out JsonElement e) && e.TryGetInt32(out int v) ? v : null;
    }

    // SESSION_CONNECTED payload의 username 추출 (없으면 null).
    private static string? GetUsername(ControlMessage msg)
    {
        if (msg.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty("username", out JsonElement u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }

    // !list 응답 라인 배열 — 온라인 세션을 depth별로 집계, 레이어당 한 줄
    // ("[L1]: 이름들"). 모드가 각 줄을 별도의 개인 채팅으로 표시한다.
    private static string[] BuildPlayerListLines(PlayerSessionStore sessions)
    {
        var byDepth = sessions.All
            .Where(s => s.Session != PlayerSessionState.Offline)
            .GroupBy(s => s.Depth)
            .OrderBy(g => g.Key)
            .ToList();
        if (byDepth.Count == 0) return new[] { "접속 중인 플레이어가 없습니다." };

        return byDepth.Select(g =>
            $"[L{g.Key}] {string.Join(", ", g.Select(s => s.Username ?? s.Key.Value))}").ToArray();
    }

    // Discord 채널 주제용 접속 인원 문자열 — "현재 n명 접속중 / [L1] 2명 / [L3] 1명".
    // 온라인 정의(MaxPlayers 검사와 동일): Session != Offline. 플레이어가 없는 레이어는 생략.
    private static string BuildPlayerCountText(PlayerSessionStore sessions)
    {
        var online = sessions.All.Where(s => s.Session != PlayerSessionState.Offline).ToList();
        var parts = new List<string> { $"현재 {online.Count}명 접속중" };
        foreach (var g in online.GroupBy(s => s.Depth).OrderBy(g => g.Key))
        {
            parts.Add($"[L{g.Key}] {g.Count()}명");
        }
        return string.Join(" / ", parts);
    }

    // 접속 인원 채널 주제 갱신 (fire-and-forget — 미연결 시 내부 no-op).
    private static void RefreshPlayerCountTopic(DiscordBot discordBot, PlayerSessionStore sessions)
    {
        _ = discordBot.UpdatePlayerCountTopicAsync(BuildPlayerCountText(sessions));
    }

    // !currentrun 응답 텍스트 — 키 조회 또는 전체 목록 (RunRuleStore가 정본).
    private static string BuildRunSettingsText(RunRuleStore runRules, string key)
    {
        var snapshot = runRules.RunSnapshot;
        if (!string.IsNullOrEmpty(key))
        {
            return snapshot.TryGetValue(key, out string? value)
                ? $"{key}: {value}"
                : $"설정을 찾을 수 없습니다: {key}";
        }
        return string.Join("\n", snapshot.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    // ── 그룹 (GROUP_REQUEST / 초대 / 채팅 라우팅) ──

    private static void SendGroupResult(ControlHub hub, PlayerSessionStore sessions, string playerKey, string[] lines)
    {
        PlayerState? state = sessions.Get(PlayerKey.FromString(playerKey));
        if (state?.InstanceId == null) return;
        ControlHub.ClientConnection? conn = hub.ModConnection(state.InstanceId);
        if (conn == null) return;
        hub.SendNoAck(conn, "GROUP_RESULT", new { playerKey, lines });
    }

    // 채팅 모드 강제 리셋 (그룹 이탈/삭제 — 그룹 모드 잔류 방지, 모드가 조용히 Global 전환).
    private static void SendChatModeReset(ControlHub hub, PlayerSessionStore sessions, string playerKey)
    {
        PlayerState? state = sessions.Get(PlayerKey.FromString(playerKey));
        if (state?.InstanceId == null) return;
        ControlHub.ClientConnection? conn = hub.ModConnection(state.InstanceId);
        if (conn == null) return;
        hub.SendNoAck(conn, "CHATMODE_RESET", new { playerKey });
    }

    private static void HandleGroupRequest(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        GroupStore groups, DiscordBot discordBot, ControlHub.ClientConnection conn, PlayerKey player, GroupRequestPayload req)
    {
        PlayerState? state = sessions.Get(player);
        string displayName = state?.Username ?? player.Value;

        switch (req.Action)
        {
            case "create":
            {
                string? err = groups.TryCreate(player, req.Name, out Group? created);
                if (err != null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { err });
                    return;
                }
                // Discord 그룹 스레드 자동 생성 (실패해도 게임 내 그룹은 정상 동작).
                _ = discordBot.CreateGroupThreadAsync(created!.Name)
                    .ContinueWith(t => groups.SetThreadId(created.Name, t.Result));
                SendGroupResult(hub, sessions, player.Value, new[] { $"그룹 [{created.Name}]을(를) 생성하고 가입했습니다." });
                break;
            }
            case "join":
            {
                string? err = groups.TryJoin(player, req.Name, out Group? joined);
                SendGroupResult(hub, sessions, player.Value,
                    err != null ? new[] { err } : new[] { $"그룹 [{joined!.Name}]에 가입했습니다." });
                break;
            }
            case "leave":
            {
                string? err = groups.Leave(player, out Group? removed);
                if (err != null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { err });
                    return;
                }
                if (removed != null)
                {
                    // 마지막 멤버 퇴장 — 그룹 삭제 + Discord 스레드 정리.
                    _ = discordBot.DeleteGroupThreadAsync(removed.DiscordThreadId);
                }
                // 그룹 모드 잔류 방지 — 조용히 Global로 리셋.
                SendChatModeReset(hub, sessions, player.Value);
                SendGroupResult(hub, sessions, player.Value, new[] { "그룹에서 퇴장했습니다." });
                break;
            }
            case "remove":
            {
                string? err = groups.Remove(player, out Group? removed);
                if (err != null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { err });
                    return;
                }
                _ = discordBot.DeleteGroupThreadAsync(removed!.DiscordThreadId);
                // 잔류 멤버 퇴출 알림 + 그룹 모드 잔류 방지 (조용히 Global 리셋).
                // 생성자(삭제 실행자)는 아래의 "삭제했습니다" 메시지로 충분 — 이중 안내 제외.
                foreach (string memberKey in removed.MemberKeys)
                {
                    if (memberKey == player.Value) continue;
                    SendChatModeReset(hub, sessions, memberKey);
                    SendGroupResult(hub, sessions, memberKey, new[] { $"그룹 [{removed.Name}]이(가) 삭제되었습니다." });
                }
                SendChatModeReset(hub, sessions, player.Value);
                SendGroupResult(hub, sessions, player.Value, new[] { $"그룹 [{removed.Name}]을(를) 삭제했습니다." });
                break;
            }
            case "list":
                SendGroupResult(hub, sessions, player.Value, groups.ListAll());
                break;
            case "players":
                SendGroupResult(hub, sessions, player.Value, groups.PlayerList(sessions, player));
                break;
            case "joinable":
            {
                string? err = groups.ToggleJoinable(player, out Group? g);
                SendGroupResult(hub, sessions, player.Value,
                    err != null ? new[] { err }
                    : new[] { $"그룹 [{g!.Name}] — 직접 가입 {(g.Joinable ? "허용" : "불가 (초대 전용)")}." });
                break;
            }
            case "invite":
            {
                // 대상 해석 (온라인 세션만 — 이름 또는 PlayerKey).
                string query = req.Target.Trim();
                PlayerState? target = sessions.All.FirstOrDefault(s =>
                    s.Session != PlayerSessionState.Offline
                    && (s.Username != null && s.Username.Equals(query, StringComparison.OrdinalIgnoreCase)
                        || s.Key.Value.Equals(query, StringComparison.OrdinalIgnoreCase)));
                if (target == null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { $"플레이어를 찾을 수 없습니다: {query}" });
                    return;
                }
                Group? myGroup = groups.GetByPlayer(player);
                if (myGroup == null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { "그룹에 가입되어 있지 않습니다." });
                    return;
                }
                if (groups.GetByPlayer(target.Key) != null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { $"{target.Username}님은 이미 그룹에 가입되어 있습니다." });
                    return;
                }
                if (target.InstanceId == null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { $"{target.Username}님의 인스턴스를 찾을 수 없습니다." });
                    return;
                }
                ControlHub.ClientConnection? targetConn = hub.ModConnection(target.InstanceId);
                if (targetConn == null)
                {
                    SendGroupResult(hub, sessions, player.Value, new[] { $"{target.Username}님의 인스턴스가 연결되지 않았습니다." });
                    return;
                }
                hub.SendNoAck(targetConn, "GROUP_INVITE", new
                {
                    playerKey = target.Key.Value,
                    groupName = myGroup.Name,
                    callerName = displayName,
                });
                SendGroupResult(hub, sessions, player.Value,
                    new[] { $"{target.Username}님에게 초대를 보냈습니다." });
                break;
            }
            default:
                SendGroupResult(hub, sessions, player.Value, new[] { $"알 수 없는 그룹 명령: {req.Action}" });
                break;
        }
    }

    private static void HandleGroupInviteResult(ControlHub hub, PlayerSessionStore sessions, GroupStore groups,
        DiscordBot discordBot, PlayerKey player, GroupInviteResultPayload result)
    {
        if (!result.Accepted)
        {
            return;
        }
        string? err = groups.AcceptInvite(player, result.GroupName, out Group? joined);
        if (err != null)
        {
            SendGroupResult(hub, sessions, player.Value, new[] { err });
            return;
        }
        PlayerState? state = sessions.Get(player);
        string displayName = state?.Username ?? player.Value;
        SendGroupResult(hub, sessions, player.Value, new[] { $"그룹 [{joined!.Name}]에 가입했습니다." });

        // 그룹 멤버들에게 가입 알림.
        foreach (string memberKey in joined.MemberKeys.Where(k => k != player.Value))
        {
            SendGroupResult(hub, sessions, memberKey, new[] { $"{displayName}님이 그룹에 가입했습니다." });
        }
    }

    // 그룹 채팅 라우팅 — 그룹 멤버가 있는 인스턴스에 targets(멤버 이름)와 함께 전송.
    // 발신자도 targets에 포함된다 (그룹 모드는 원본 브로드캐스트가 억제되므로 중복 없음).
    // senderKey: 게임 유저 발신 여부 — null이면 Discord 스레드 출처 (스레드 재에코 방지).
    private static void RouteGroupChat(ControlHub hub, PlayerSessionStore sessions, GroupStore groups,
        DiscordBot discordBot, Group group, string? senderKey,
        string speaker, string message, string color, string layerLabel)
    {
        // 온라인 멤버를 인스턴스별로 묶어 전송 — 각 인스턴스는 targets(멤버 이름)로 필터.
        var byInstance = group.MemberKeys
            .Select(k => PlayerKey.FromString(k))
            .Select(k => (Key: k, State: sessions.Get(k)))
            .Where(x => x.State != null && x.State.Session != PlayerSessionState.Offline)
            .Where(x => x.State!.InstanceId != null)
            .GroupBy(x => x.State!.InstanceId!);

        foreach (var instGroup in byInstance)
        {
            ControlHub.ClientConnection? modConn = hub.ModConnection(instGroup.Key);
            if (modConn == null) continue;
            string[] targets = instGroup.Select(x => x.State!.Username ?? x.Key.Value).ToArray();
            bool fromDiscord = senderKey == null;
            hub.SendNoAck(modConn, "CHAT", new
            {
                speaker,
                message,
                color,
                layer = layerLabel,
                mode = "group",
                targets,
                prefix = group.Name,
                prefixColor = GroupColor(group.Name),
                // Discord 스레드 발신 — 전체 채팅과 동일한 "D" 파란색 배지.
                badge = fromDiscord ? "D" : "",
                badgeColor = fromDiscord ? "#5865F2" : "",
            });
        }

        // 게임 유저 발신 메시지만 Discord 그룹 스레드로 전송 (스레드 출처 메시지 재에코 방지).
        if (senderKey != null && group.DiscordThreadId != 0)
        {
            _ = discordBot.SendGroupChatAsync(group.DiscordThreadId, speaker, message, layerLabel);
        }
    }

    // 그룹명 → 안정적 배지 색상 (팔레트 해시).
    private static string GroupColor(string name)
    {
        string[] palette =
        {
            "#87CEEB", "#98FB98", "#FFD700", "#FFA07A",
            "#DDA0DD", "#87CEFA", "#F4A460", "#AFEEEE",
        };
        int h = 0;
        foreach (char c in name) h = (h * 31 + c) % palette.Length;
        return palette[h];
    }


    private sealed class HelloVersion { public int Version { get; set; } }
    private sealed class AgentHelloPayload { public string MachineId { get; set; } = ""; public int Capacity { get; set; } public string Address { get; set; } = ""; }
    private sealed class InstanceExitedPayload { public string InstanceKey { get; set; } = ""; public int Code { get; set; } }
    private sealed class InstanceReadyPayload { public string InstanceKey { get; set; } = ""; }
    private sealed class InstanceFaultPayload { public string InstanceKey { get; set; } = ""; public string? Reason { get; set; } }
    private sealed class LayerEndPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } public int MaxLayers { get; set; } }

    // !respawn 요청 (mod → orchestrator) — fromDepth는 발신 인스턴스 실제 depth.
    private sealed class RespawnPayload { public string PlayerKey { get; set; } = ""; public int FromDepth { get; set; } }

    // 실시간 로그 릴레이 (agent/gateway → orchestrator — LOG).
    private sealed class LogPayload { public string Source { get; set; } = ""; public string Message { get; set; } = ""; }
    private sealed class PlayerKeyPayload { public string? PlayerKey { get; set; } }
    private sealed class InstancePayload { public string InstanceId { get; set; } = ""; }
    private sealed class SwapFailedPayload { public string? Reason { get; set; } }

    // 크로스 인스턴스 채팅 payload (mod → orchestrator → mod).
    private sealed class ChatPayload
    {
        public string Speaker { get; set; } = "";
        public string Message { get; set; } = "";
        public string Color { get; set; } = "";
        public string Layer { get; set; } = "";
        public string Mode { get; set; } = "";          // "group" — 그룹 채팅
        public string PlayerKey { get; set; } = "";     // 발신자 (그룹 라우팅용)
        public string[] Targets { get; set; } = [];     // 수신할 멤버 이름
        public string Prefix { get; set; } = "";        // 그룹명 배지
        public string PrefixColor { get; set; } = "";
        public string Badge { get; set; } = "";         // Discord 그룹 채팅의 "D" 배지
        public string BadgeColor { get; set; } = "";
    }

    // 그룹 명령 요청 (mod → orchestrator).
    private sealed class GroupRequestPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Action { get; set; } = "";
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
    }

    // 그룹 명령 결과 (orchestrator → mod — 개인 회신 라인).
    private sealed class GroupResultPayload
    {
        public string PlayerKey { get; set; } = "";
        public string[] Lines { get; set; } = [];
    }

    // 그룹 초대 요청 (orchestrator → 대상 인스턴스 mod — 단일 유저 투표).
    private sealed class GroupInvitePayload
    {
        public string PlayerKey { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string CallerName { get; set; } = "";
    }

    // 초대 투표 결과 (mod → orchestrator).
    private sealed class GroupInviteResultPayload
    {
        public string PlayerKey { get; set; } = "";
        public string GroupName { get; set; } = "";
        public bool Accepted { get; set; }
        public string Reason { get; set; } = "";
    }

    // 채팅 모드 전환 요청 (mod → orchestrator — Group 진입 검증).
    private sealed class ChatModeRequestPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Mode { get; set; } = "";
    }

    // 채팅 모드 전환 결과 (orchestrator → mod).
    private sealed class ChatModeResultPayload
    {
        public string PlayerKey { get; set; } = "";
        public bool Ok { get; set; }
    }

    // 채팅 모드 강제 리셋 (orchestrator → mod — 그룹 이탈/삭제 시 Global).
    private sealed class ChatModeResetPayload
    {
        public string PlayerKey { get; set; } = "";
    }

    // !list 요청 (mod → orchestrator).
    private sealed class ListRequestPayload
    {
        public string PlayerKey { get; set; } = "";
    }

    // !currentrun 요청 (mod → orchestrator).
    private sealed class CurrentRequestPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Key { get; set; } = "";
    }

    // !discord 요청 (mod → orchestrator).
    private sealed class DiscordRequestPayload
    {
        public string PlayerKey { get; set; } = "";
    }

    // !calladmin 보고 (mod → orchestrator — Discord는 후속).
    private sealed class CallAdminPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Username { get; set; } = "";
    }

    // 사망/리스폰 이벤트 (mod → orchestrator — D1 Discord 알림).
    private sealed class PlayerEventPayload
    {
        public string PlayerKey { get; set; } = "";
        public string Username { get; set; } = "";
        public string Layer { get; set; } = "";
    }

    // 크로스 인스턴스 시스템 공지 (mod ↔ orchestrator ↔ mod — 사망/마이그레이션).
    private sealed class AnnouncePayload
    {
        public string Kind { get; set; } = "";
        public string PlayerKey { get; set; } = "";
        public string Name { get; set; } = "";
        public int FromDepth { get; set; }
        public int ToDepth { get; set; }
    }

    // PlayerKey에서 SteamID64 추출 — STEAM_ 접두사가 아니면 0 (직접연결/steam 비활성).
    private static ulong SteamIdOf(PlayerKey key) =>
        key.Value.StartsWith("STEAM_") && ulong.TryParse(key.Value.AsSpan(6), out ulong sid) ? sid : 0;


    // 세션별 생존 상태 — DIED/RESPAWNED 이벤트로 갱신, 접속 시 기본 생존,
    // 퇴장 시 제거. livingCount(=로비 KeyLivingCount) 산출에 사용 (실시간 반영).
    private static readonly Dictionary<PlayerKey, bool> _alive = new();

    private static DateTime _lastLobbyMetadataPush = DateTime.MinValue;
    // 안전망 주기: 이벤트 구동이 기본 — 드문 푸시 유실/엣지 케이스의
    // 자가 치유용 저빈도 폴링 (접속/퇴장/사망/리스폰/rule 명령/마이그레이션 시 force 푸시가 즉시 반영).
    private static readonly TimeSpan LobbyMetadataInterval = TimeSpan.FromSeconds(30);

    // Steam 로비 동적 메타데이터 푸시: livingCount = 온라인 세션 중 생존 상태 수,
    // happinessSum = 0 (기본값 고정), steamIds = STEAM_ 세션 목록,
    // rulesBase64 = rule.json 기반 규칙 구조체 (RulesBlobBuilder — 규칙 단일 정본).
    // mod 목록은 전송하지 않는다 (개조 전용 모드뿐 — 게이트웨이가 빈 값으로 고정).
    // 30초 안전망 스로틀 — force=true면 즉시 (게이트웨이 재연결/세션 변동/사망·리스폰/rule 명령 시).
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

// 밴 목록 — 중앙 원본, 게이트웨이에 BAN 명령 전파 (O6-8).
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

    // 전체 목록 (게이트웨이 AUTH_INFO 푸시용).
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
