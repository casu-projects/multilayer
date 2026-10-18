namespace CasuMpServer;

internal sealed class SessionLifecycle
{
    private readonly ClientGateway _gateway;
    private readonly PlayerSessionStore _sessions;
    private readonly MigrationCoordinator _migrations;
    private readonly LobbyMetadataService _lobby;
    private readonly DiscordBot _discord;
    private readonly ControlHub _hub;

    public SessionLifecycle(ClientGateway gateway, PlayerSessionStore sessions,
        MigrationCoordinator migrations, LobbyMetadataService lobby, DiscordBot discord, ControlHub hub)
    {
        _gateway = gateway;
        _sessions = sessions;
        _migrations = migrations;
        _lobby = lobby;
        _discord = discord;
        _hub = hub;
    }

    public void OnConnected(ClientSession session)
    {
        PlayerKey key = session.Player;
        if (!_sessions.OnSessionConnected(key, session.Username))
        {
            _gateway.KickSilent(session, "Server is busy, please try again.");
            return;
        }

        _lobby.SetAlive(key, true);
        _lobby.Push(force: true);

        string joinName = session.Username.Length > 0 ? session.Username : key.Value;
        foreach (var modConn in _hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            _hub.SendNoAck(modConn, MessageTypes.Announce, new
            {
                kind = "join",
                playerKey = key.Value,
                name = joinName,
                fromDepth = 0,
                toDepth = 0,
                originInstance = _sessions.Get(key)?.InstanceId ?? "",
            });
        }

        _ = _discord.SendJoinLeaveAsync(joinName, LobbyMetadataService.SteamIdOf(key), joined: true);
        _lobby.RefreshTopic();
    }

    public void OnDisconnected(ClientSession session, string reason)
    {
        PlayerKey key = session.Player;
        if (_migrations.IsMigrating(key))
        {
            _migrations.OnPlayerQuitDuringMigration(key);
        }
        else
        {
            _sessions.OnSessionDisconnected(key);
            _lobby.Remove(key);
            _lobby.Push(force: true);
        }

        PlayerState? state = _sessions.Get(key);
        string name = state?.Username ?? key.Value;

        _ = _discord.SendJoinLeaveAsync(name, LobbyMetadataService.SteamIdOf(key), joined: false);
        foreach (var modConn in _hub.Connections.Where(c => c.Kind == ClientKind.Mod && !c.Closed))
        {
            _hub.SendNoAck(modConn, MessageTypes.Announce, new
            {
                kind = "leave",
                playerKey = key.Value,
                name,
                fromDepth = 0,
                toDepth = 0,
                originInstance = state?.InstanceId ?? "",
            });
        }

        _lobby.RefreshTopic();
    }

    public void OnBackendConnected(ClientSession session)
    {
        if (session.InstanceId is not { Length: > 0 } instanceId) return;
        _sessions.OnBackendConnected(session.Player, instanceId);
        _migrations.OnBackendConnected(session.Player, instanceId, null);
    }
}
