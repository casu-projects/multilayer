using CasuMp.Protocol;
using System.Text.Json;

namespace CasuMpServer;

internal sealed class ModMessageHandler
{
    private readonly ControlHub hub;
    private readonly PlayerSessionStore sessions;
    private readonly InstanceManager instances;
    private readonly MigrationCoordinator migrations;
    private readonly PlayerDataStore dataStore;
    private readonly RunRuleStore runRules;
    private readonly VoteService voteService;
    private readonly DiscordBot discordBot;
    private readonly LobbyMetadataService lobby;
    private readonly WorldSnapshotStore mapStore;
    private readonly PlayerDetailRegistry playerDetails;
    private readonly ChatHistoryStore chatHistory;
    private readonly ObjectBackgroundStore objectBackgrounds;
    private readonly RequestRegistry<MapObjectQueryResponse> objectQueries;
    private readonly RequestRegistry<MapObjectDetailResponse> objectDetails;

    public ModMessageHandler(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, PlayerDataStore dataStore, RunRuleStore runRules,
        VoteService voteService, DiscordBot discordBot, LobbyMetadataService lobby,
        WorldSnapshotStore mapStore, PlayerDetailRegistry playerDetails, ChatHistoryStore chatHistory,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails)
    {
        this.hub = hub;
        this.sessions = sessions;
        this.instances = instances;
        this.migrations = migrations;
        this.dataStore = dataStore;
        this.runRules = runRules;
        this.voteService = voteService;
        this.discordBot = discordBot;
        this.lobby = lobby;
        this.mapStore = mapStore;
        this.playerDetails = playerDetails;
        this.chatHistory = chatHistory;
        this.objectBackgrounds = objectBackgrounds;
        this.objectQueries = objectQueries;
        this.objectDetails = objectDetails;
    }

    public void Dispatch(ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.PlayerPositions:
                if (msg.PayloadAs<PlayerPositionsPayload>() is { } positions)
                {
                    if (string.IsNullOrEmpty(positions.InstanceKey))
                        positions.InstanceKey = conn.InstanceKey ?? "";
                    mapStore.UpdatePlayers(positions);
                }
                break;
            case MessageTypes.PlayerDetailResponse:
                if (msg.PayloadAs<PlayerDetailResponse>() is { } detail)
                    playerDetails.Resolve(detail);
                break;
            case MessageTypes.ObjectQueryResponse:
                if (msg.PayloadAs<MapObjectQueryResponse>() is { } objectQuery)
                    objectQueries.Resolve(objectQuery.RequestId, objectQuery);
                break;
            case MessageTypes.ObjectDetailResponse:
                if (msg.PayloadAs<MapObjectDetailResponse>() is { } objectDetail)
                    objectDetails.Resolve(objectDetail.RequestId, objectDetail);
                break;
            case MessageTypes.ObjectBackgrounds:
                if (msg.PayloadAs<MapBackgroundPayload>() is { } backgroundPayload
                    && !string.IsNullOrEmpty(backgroundPayload.InstanceKey))
                    objectBackgrounds.Set(backgroundPayload.InstanceKey, backgroundPayload.Objects);
                break;
            case MessageTypes.InstanceReady:
                if (msg.PayloadAs<InstanceReadyPayload>() is { } ready
                    && instances.OnInstanceReady(ready.InstanceKey))
                {
                    sessions.PushRoutesForInstance(ready.InstanceKey);
                    migrations.OnInstanceReady(ready.InstanceKey); // READY -> 라우팅/SWAP
                }
                break;
            case MessageTypes.InstanceFault:
                if (msg.PayloadAs<InstanceFaultPayload>() is { } fault)
                    instances.OnInstanceFault(fault.InstanceKey, fault.Reason ?? "unknown");
                break;
            case MessageTypes.LayerEnd:
                if (msg.PayloadAs<LayerEndPayload>() is { } layerEnd && Program.TryPlayerKey(layerEnd.PlayerKey, out var le))
                    migrations.OnLayerEnd(le, layerEnd.FromDepth, layerEnd.MaxLayers); // 레이어 종료 -> 마이그레이션
                break;
            case MessageTypes.Respawn:
                if (msg.PayloadAs<RespawnPayload>() is { } respawn && Program.TryPlayerKey(respawn.PlayerKey, out var rk))
                    migrations.OnRespawnRequest(rk, respawn.FromDepth); // !respawn
                break;
            case MessageTypes.FreezeDone:
                if (Program.TryPlayerKey(msg, out var fd))
                    migrations.OnFreezeDone(fd, Program.GetEpoch(msg));
                break;
            case MessageTypes.PreClearDone:
                if (Program.TryPlayerKey(msg, out var pc))
                    migrations.OnPreClearDone(pc, Program.GetEpoch(msg));
                break;
            case MessageTypes.ResumeDone:
                if (Program.TryPlayerKey(msg, out var rd))
                    migrations.OnResumeDone(rd, Program.GetEpoch(msg));
                break;
            case MessageTypes.WorldgenDone:
                if (Program.TryPlayerKey(msg, out var wg))
                    migrations.OnWorldgenDone(wg, Program.GetEpoch(msg));
                break;
            case MessageTypes.PlayerLeft:
                if (Program.TryPlayerKey(msg, out var pl))
                    migrations.OnPlayerLeftDuringMigration(pl, Program.GetEpoch(msg));
                break;
            case MessageTypes.PlayerDataSubmit:
                if (Program.TryPlayerKey(msg, out var ps) && msg.Payload is { } submitPayload
                    && submitPayload.TryGetProperty("payload", out JsonElement innerPayload))
                {
                    bool migration = migrations.IsMigrating(ps);
                    dataStore.OnSubmit(ps, innerPayload, migration); // 마이그레이션 중이면 보류
                }
                break;
            case MessageTypes.PlayerDataRequest:
                if (Program.TryPlayerKey(msg, out var pr))
                    dataStore.OnRequest(pr, conn, hub, migrations.IsMigrating(pr));
                break;
            case MessageTypes.Chat:
                if (msg.PayloadAs<ChatPayload>() is { } chat && !string.IsNullOrEmpty(chat.Speaker))
                {
                    string layerLabel = $"L{conn.InstanceDepth}";
                    if (chat.Speaker != "*")
                        chatHistory.Add("game", chat.Speaker, layerLabel, chat.Color ?? "", chat.Message);
                    int forwarded = 0;
                    foreach (ControlHub.ClientConnection other in hub.Connections
                        .Where(c => c.Kind == ClientKind.Mod && !c.Closed && c != conn))
                    {
                        hub.SendNoAck(other, MessageTypes.Chat, new
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
            case MessageTypes.Died:
            case MessageTypes.Respawned:
                if (msg.PayloadAs<PlayerEventPayload>() is { } deathEvent)
                {
                    if (Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey))
                    {
                        lobby.SetAlive(evKey, msg.Type == MessageTypes.Respawned); // 생존 상태 갱신
                        lobby.Push(force: true);
                    }

                    bool migrating = Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey2)
                        && migrations.IsMigrating(evKey2);
                    if (!migrating)
                    {
                        _ = discordBot.SendDeathRespawnAsync(
                            deathEvent.Username ?? deathEvent.PlayerKey ?? "?",
                            Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey3) ? LobbyMetadataService.SteamIdOf(evKey3) : 0,
                            died: msg.Type == MessageTypes.Died,
                            deathEvent.Layer ?? "");

                        if (msg.Type == MessageTypes.Respawned)
                        {
                            string respawnName = deathEvent.Username ?? deathEvent.PlayerKey ?? "?";
                            foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                            {
                                hub.SendNoAck(modConn, MessageTypes.Announce, new
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
            case MessageTypes.ListRequest:
                if (msg.PayloadAs<ListRequestPayload>() is { } listReq)
                {
                    string[] lines = lobby.BuildPlayerListLines();
                    hub.SendNoAck(conn, MessageTypes.ListResult, new { playerKey = listReq.PlayerKey, lines });
                }
                break;
            case MessageTypes.CallAdmin:
                if (msg.PayloadAs<CallAdminPayload>() is { } callAdmin)
                {
                    bool callOk = Program.TryPlayerKey(callAdmin.PlayerKey, out var callKey);
                    _ = discordBot.SendCallAdminAsync(callAdmin.Username ?? callKey.Value ?? "?",
                        callOk ? LobbyMetadataService.SteamIdOf(callKey) : 0);
                }
                break;
            case MessageTypes.Announce:
                if (msg.PayloadAs<AnnouncePayload>() is { } announce)
                {
                    foreach (var other in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(other, MessageTypes.Announce, new
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
            case MessageTypes.VoteStart:
                if (msg.PayloadAs<VoteStartMarker>() is { } voteStart)
                {
                    voteService.HandleStart(conn, voteStart);
                }
                break;
            case MessageTypes.VoteTally:
                if (msg.PayloadAs<VoteTallyMarker>() is { } tally)
                {
                    voteService.HandleTally(conn, tally);
                }
                break;
            default:
                break;
        }
        }
}
