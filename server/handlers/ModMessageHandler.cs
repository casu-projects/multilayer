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
    private readonly DiscordBot discordBot;
    private readonly LobbyMetadataService lobby;
    private readonly WorldSnapshotStore mapStore;
    private readonly PlayerDetailRegistry playerDetails;
    private readonly ObjectBackgroundStore objectBackgrounds;
    private readonly RequestRegistry<MapObjectQueryResponse> objectQueries;
    private readonly RequestRegistry<MapObjectDetailResponse> objectDetails;

    public ModMessageHandler(ControlHub hub, PlayerSessionStore sessions, InstanceManager instances,
        MigrationCoordinator migrations, PlayerDataStore dataStore, RunRuleStore runRules,
        DiscordBot discordBot, LobbyMetadataService lobby,
        WorldSnapshotStore mapStore, PlayerDetailRegistry playerDetails,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails)
    {
        this.hub = hub;
        this.sessions = sessions;
        this.instances = instances;
        this.migrations = migrations;
        this.dataStore = dataStore;
        this.runRules = runRules;
        this.discordBot = discordBot;
        this.lobby = lobby;
        this.mapStore = mapStore;
        this.playerDetails = playerDetails;
        this.objectBackgrounds = objectBackgrounds;
        this.objectQueries = objectQueries;
        this.objectDetails = objectDetails;
    }

    public void Dispatch(ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.PLAYER_POSITIONS:
                if (msg.PayloadAs<PlayerPositionsPayload>() is { } positions)
                {
                    if (string.IsNullOrEmpty(positions.InstanceKey))
                        positions.InstanceKey = conn.InstanceKey ?? "";
                    mapStore.UpdatePlayers(positions);
                }
                break;
            case MessageType.PLAYER_DETAIL_RESPONSE:
                if (msg.PayloadAs<PlayerDetailResponse>() is { } detail)
                    playerDetails.Resolve(detail);
                break;
            case MessageType.OBJECT_QUERY_RESPONSE:
                if (msg.PayloadAs<MapObjectQueryResponse>() is { } objectQuery)
                    objectQueries.Resolve(objectQuery.RequestId, objectQuery);
                break;
            case MessageType.OBJECT_DETAIL_RESPONSE:
                if (msg.PayloadAs<MapObjectDetailResponse>() is { } objectDetail)
                    objectDetails.Resolve(objectDetail.RequestId, objectDetail);
                break;
            case MessageType.OBJECT_BACKGROUNDS:
                if (msg.PayloadAs<MapBackgroundPayload>() is { } backgroundPayload
                    && !string.IsNullOrEmpty(backgroundPayload.InstanceKey))
                    objectBackgrounds.Set(backgroundPayload.InstanceKey, backgroundPayload.Objects);
                break;
            case MessageType.MAP_WORLD_BEGIN:
                if (msg.PayloadAs<MapWorldBegin>() is { } worldBegin)
                {
                    bool hasInstance = !string.IsNullOrEmpty(conn.InstanceKey);
                    var worldResult = hasInstance ? mapStore.BeginWorld(conn.InstanceKey!, worldBegin) : null;
                    hub.SendNoAck(conn, MessageType.MAP_WORLD_ACK, new MapUploadAck
                    {
                        RequestId = worldBegin.RequestId,
                        Ok = worldResult != null,
                        WorldVersion = worldBegin.WorldVersion,
                        Version = worldResult?.Version ?? 0,
                        Complete = worldResult != null && (long)worldBegin.Width * worldBegin.Height == 0,
                        Error = worldResult == null ? "invalid world begin" : "",
                    });
                }
                break;
            case MessageType.MAP_WORLD_CHUNK:
                if (msg.PayloadAs<MapWorldChunk>() is { } worldChunk)
                {
                    bool hasInstance = !string.IsNullOrEmpty(conn.InstanceKey);
                    var chunkResult = hasInstance ? mapStore.ApplyWorldChunk(conn.InstanceKey!, worldChunk) : null;
                    hub.SendNoAck(conn, MessageType.MAP_WORLD_ACK, new MapUploadAck
                    {
                        RequestId = worldChunk.RequestId,
                        Ok = chunkResult != null,
                        WorldVersion = worldChunk.WorldVersion,
                        Version = chunkResult?.Version ?? 0,
                        Received = chunkResult?.Received ?? 0,
                        Complete = chunkResult?.Complete ?? false,
                        Error = chunkResult == null ? "invalid world chunk" : "",
                    });
                }
                break;
            case MessageType.MAP_BLOCKS_UPLOAD:
                if (msg.PayloadAs<MapBlockUpload>() is { } blockUpload)
                {
                    bool hasInstance = !string.IsNullOrEmpty(conn.InstanceKey);
                    int? version = hasInstance ? mapStore.ApplyBlocks(conn.InstanceKey!, blockUpload) : null;
                    hub.SendNoAck(conn, MessageType.MAP_BLOCKS_ACK, new MapUploadAck
                    {
                        RequestId = blockUpload.RequestId,
                        Ok = version != null,
                        WorldVersion = blockUpload.WorldVersion,
                        Version = version ?? 0,
                        Error = version == null ? "unknown instance or world version mismatch" : "",
                    });
                }
                break;
            case MessageType.INSTANCE_READY:
                if (msg.PayloadAs<InstanceReadyPayload>() is { } ready
                    && instances.OnInstanceReady(ready.InstanceKey))
                {
                    if (instances.ShouldSuspendAfterReset(ready.InstanceKey))
                    {
                        string suspendReason = instances.Find(ready.InstanceKey)?.SuspendReasonAfterReset ?? "reset";
                        instances.ClearSuspendAfterReset(ready.InstanceKey);
                        bool needed = sessions.All.Any(p => p.InstanceId == ready.InstanceKey
                                && p.Session == PlayerSessionState.Connecting)
                            || migrations.CountTargeting(ready.InstanceKey) > 0
                            || (instances.Find(ready.InstanceKey)?.Depth == 1
                                && sessions.All.Any(p => p.Session != PlayerSessionState.Offline));
                        if (!needed)
                        {
                            if (!instances.SuspendInstance(ready.InstanceKey, suspendReason))
                                Log.Info($"{ready.InstanceKey} 재동결 실패.");
                            break;
                        }
                    }

                    sessions.PushRoutesForInstance(ready.InstanceKey);
                    migrations.OnInstanceReady(ready.InstanceKey); // READY -> 라우팅/SWAP
                }
                break;
            case MessageType.INSTANCE_FAULT:
                if (msg.PayloadAs<InstanceFaultPayload>() is { } fault)
                    instances.OnInstanceFault(fault.InstanceKey, fault.Reason ?? "unknown");
                break;
            case MessageType.LAYER_END:
                if (msg.PayloadAs<LayerEndPayload>() is { } layerEnd && Program.TryPlayerKey(layerEnd.PlayerKey, out var le))
                    migrations.OnLayerEnd(le, layerEnd.FromDepth, layerEnd.MaxLayers); // 레이어 종료 -> 마이그레이션
                break;
            case MessageType.RESPAWN:
                if (msg.PayloadAs<RespawnPayload>() is { } respawn && Program.TryPlayerKey(respawn.PlayerKey, out var rk))
                    migrations.OnRespawnRequest(rk, respawn.FromDepth); // !respawn
                break;
            case MessageType.FREEZE_DONE:
                if (Program.TryPlayerKey(msg, out var fd))
                    migrations.OnFreezeDone(fd, Program.GetEpoch(msg), conn.InstanceKey);
                break;
            case MessageType.PRECLEAR_DONE:
                if (Program.TryPlayerKey(msg, out var pc))
                    migrations.OnPreClearDone(pc, Program.GetEpoch(msg), conn.InstanceKey);
                break;
            case MessageType.RESUME_DONE:
                if (Program.TryPlayerKey(msg, out var rd))
                    migrations.OnResumeDone(rd, Program.GetEpoch(msg), conn.InstanceKey);
                break;
            case MessageType.WORLDGEN_DONE:
                if (Program.TryPlayerKey(msg, out var wg))
                    migrations.OnWorldgenDone(wg, Program.GetEpoch(msg), conn.InstanceKey);
                break;
            case MessageType.PLAYER_LEFT:
                if (Program.TryPlayerKey(msg, out var pl))
                    migrations.OnPlayerLeftDuringMigration(pl, Program.GetEpoch(msg), conn.InstanceKey);
                break;
            case MessageType.PLAYER_DATA_SUBMIT:
                if (Program.TryPlayerKey(msg, out var ps) && msg.Payload is { } submitPayload
                    && submitPayload.TryGetProperty("payload", out JsonElement innerPayload))
                {
                    bool migration = migrations.IsMigrating(ps);
                    dataStore.OnSubmit(ps, innerPayload, migration); // 마이그레이션 중이면 보류
                }
                break;
            case MessageType.PLAYER_DATA_REQUEST:
                if (Program.TryPlayerKey(msg, out var pr))
                    dataStore.OnRequest(pr, conn, hub, migrations.IsMigrating(pr));
                break;
            case MessageType.CHAT:
                if (msg.PayloadAs<ChatPayload>() is { } chat && !string.IsNullOrEmpty(chat.Speaker))
                {
                    string layerLabel = $"L{conn.InstanceDepth}";
                    int forwarded = 0;
                    foreach (ControlHub.ClientConnection other in hub.Connections
                        .Where(c => c.Kind == ClientKind.Mod && !c.Closed && c != conn))
                    {
                        hub.SendNoAck(other, MessageType.CHAT, new
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
            case MessageType.DIED:
            case MessageType.RESPAWNED:
                if (msg.PayloadAs<PlayerEventPayload>() is { } deathEvent)
                {
                    if (Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey))
                    {
                        lobby.SetAlive(evKey, msg.Type == MessageType.RESPAWNED); // 생존 상태 갱신
                        lobby.Push(force: true);
                    }

                    bool migrating = Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey2)
                        && migrations.IsMigrating(evKey2);
                    if (!migrating)
                    {
                        _ = discordBot.SendDeathRespawnAsync(
                            deathEvent.Username ?? deathEvent.PlayerKey ?? "?",
                            Program.TryPlayerKey(deathEvent.PlayerKey, out var evKey3) ? LobbyMetadataService.SteamIdOf(evKey3) : 0,
                            died: msg.Type == MessageType.DIED,
                            deathEvent.Layer ?? "");

                        if (msg.Type == MessageType.RESPAWNED)
                        {
                            string respawnName = deathEvent.Username ?? deathEvent.PlayerKey ?? "?";
                            foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                            {
                                hub.SendNoAck(modConn, MessageType.ANNOUNCE, new
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
            case MessageType.LIST_REQUEST:
                if (msg.PayloadAs<ListRequestPayload>() is { } listReq)
                {
                    string[] lines = lobby.BuildPlayerListLines();
                    hub.SendNoAck(conn, MessageType.LIST_RESULT, new { playerKey = listReq.PlayerKey, lines });
                }
                break;
            case MessageType.ANNOUNCE:
                if (msg.PayloadAs<AnnouncePayload>() is { } announce)
                {
                    foreach (var other in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(other, MessageType.ANNOUNCE, new
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
            default:
                break;
        }
        }
}
