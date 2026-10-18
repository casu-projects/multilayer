using CasuMp.Protocol;

namespace CasuMpOrchestrator;

internal sealed class GatewayMessageHandler
{
    private readonly OrchestratorConfig config;
    private readonly ControlHub hub;
    private readonly PlayerSessionStore sessions;
    private readonly MigrationCoordinator migrations;
    private readonly RunRuleStore runRules;
    private readonly DiscordBot discordBot;
    private readonly LobbyMetadataService lobby;

    public GatewayMessageHandler(OrchestratorConfig config, ControlHub hub, PlayerSessionStore sessions,
        MigrationCoordinator migrations, RunRuleStore runRules, DiscordBot discordBot, LobbyMetadataService lobby)
    {
        this.config = config;
        this.hub = hub;
        this.sessions = sessions;
        this.migrations = migrations;
        this.runRules = runRules;
        this.discordBot = discordBot;
        this.lobby = lobby;
    }

    public void Dispatch(ControlHub.ClientConnection conn, ControlMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.SessionConnected:
                if (Program.TryPlayerKey(msg, out var connected))
                {
                    int online = sessions.All.Count(s => s.Session != PlayerSessionState.Offline);
                    if (online >= config.MaxPlayers)
                    {
                        Log.Info($"인원 초과 — {connected} 접속 거부 (최대 {config.MaxPlayers}).");
                        hub.Send(hub.GatewayConnection, MessageTypes.Kick,
                            new { playerKey = connected.Value, reason = "Server is full." });
                        break;
                    }
                    sessions.OnSessionConnected(connected, Program.GetUsername(msg));

                    lobby.SetAlive(connected, true);
                    lobby.Push(force: true);

                    string joinName = Program.GetUsername(msg) ?? connected.Value;
                    foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(modConn, MessageTypes.Announce, new
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
                        LobbyMetadataService.SteamIdOf(connected), joined: true);
                    lobby.RefreshTopic();
                }
                break;
            case MessageTypes.SessionDisconnected:
                if (Program.TryPlayerKey(msg, out var disc))
                {
                    if (migrations.IsMigrating(disc))
                    {
                        migrations.OnPlayerQuitDuringMigration(disc);
                    }
                    else
                    {
                        sessions.OnSessionDisconnected(disc);

                        lobby.Remove(disc);
                        lobby.Push(force: true);
                    }

                    var discState = sessions.Get(disc);
                    string discName = discState?.Username ?? disc.Value;
                    _ = discordBot.SendJoinLeaveAsync(discName, LobbyMetadataService.SteamIdOf(disc), joined: false);
                    foreach (var modConn in hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
                    {
                        hub.SendNoAck(modConn, MessageTypes.Announce, new
                        {
                            kind = "leave",
                            playerKey = disc.Value,
                            name = discName,
                            fromDepth = 0,
                            toDepth = 0,
                            originInstance = discState?.InstanceId ?? "",
                        });
                    }

                    lobby.RefreshTopic();
                }
                break;
            case MessageTypes.BackendConnected:
                if (Program.TryPlayerKey(msg, out var bc) && msg.PayloadAs<InstancePayload>() is { } bcPayload)
                {
                    sessions.OnBackendConnected(bc, bcPayload.InstanceId);
                    migrations.OnBackendConnected(bc, bcPayload.InstanceId, Program.GetEpoch(msg)); // 스왑 완료
                }
                break;
            case MessageTypes.SwapFailed:
                if (Program.TryPlayerKey(msg, out var sf) && msg.PayloadAs<SwapFailedPayload>() is { } sfPayload)
                    migrations.OnSwapFailed(sf, sfPayload.Reason ?? "unknown"); // 스왑 실패 롤백
                break;
            default:
                break;
        }
        }
}
