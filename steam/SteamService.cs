using System.Collections.Concurrent;
using CasuMp.SteamSession;

namespace CasuMpSteam;

public sealed class SteamService
{
    private const string FallbackServerName = "CasuMP Server";

    private readonly SteamConfig _config;
    private readonly SteamLobbyHost _lobby;
    private readonly SteamP2PBridge _p2p;
    private readonly LobbyCatalog _catalog = new();
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly object _loginLock = new();

    private LoginSession? _login;
    private bool _restartLobbyAfterLogin;
    private bool _started;

    public SteamService(SteamConfig config)
    {
        _config = config;
        _lobby = new SteamLobbyHost(config.SessionPath, FallbackServerName);
        _p2p = new SteamP2PBridge(config, _lobby);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _p2p.Start();
        _lobby.Start();
    }

    public void Tick()
    {
        try
        {
            _lobby.Tick();
            _p2p.PollEvents();

            while (_actions.TryDequeue(out Action? action))
            {
                try { action(); }
                catch (Exception ex) { SteamLog.Info($"작업 처리 실패: {ex.Message}"); }
            }

            bool restart;
            lock (_loginLock)
            {
                restart = _restartLobbyAfterLogin;
                _restartLobbyAfterLogin = false;
            }
            if (restart)
            {
                _lobby.Start();
            }
        }
        catch (Exception ex)
        {
            SteamLog.Info($"Tick 오류: {ex.Message}");
        }
    }

    public void Stop()
    {
        _started = false;
        _p2p.Stop();
        _lobby.Stop();
        lock (_loginLock) { _login?.Cancel(); }
    }

    public void PostMetadata(LobbyMetadataPayload payload) =>
        _actions.Enqueue(() => _lobby.UpdateMetadata(payload));

    public void Reload() => _actions.Enqueue(() => _lobby.Recycle());

    public async Task<LobbyListResponse> GetLobbiesAsync(string? versionFilter, CancellationToken ct)
    {
        SteamKit2.SteamMatchmaking.GetLobbyListCallback callback = await _lobby.GetLobbyListAsync(ct);
        return await _catalog.BuildAsync(callback, versionFilter, _config.WebApiKey);
    }

    public Task<string?> GetAvatarAsync(ulong steamId, CancellationToken ct) =>
        _catalog.GetAvatarAsync(steamId, _config.WebApiKey, ct);

    public object GetStatus()
    {
        lock (_loginLock)
        {
            return new
            {
                state = _lobby.State.ToString(),
                loggedOn = _lobby.LoggedOn,
                lobbyId = _lobby.LobbyId is ulong id ? id.ToString() : null,
                sessionLoaded = File.Exists(_config.SessionPath),
                p2p = _p2p.Initialized,
                bridgeConnections = _p2p.BridgeCount,
                login = _login?.Snapshot(),
                rest = _config.RestListen,
                coreBridge = _config.CoreBridgeAddr,
            };
        }
    }

    public LoginSession? StartLogin(string username, string password, bool preferCode = false)
    {
        lock (_loginLock)
        {
            if (_login is { State: "pending" or "starting" or "waiting_code" or "waiting_confirmation" or "authenticating" or "saving" })
            {
                return null;
            }

            var session = new LoginSession(allowDeviceConfirmation: !preferCode);
            _login = session;
            _actions.Enqueue(() => BeginLogin(session, username, password));
            return session;
        }
    }

    public LoginSession? GetLogin(string id)
    {
        lock (_loginLock)
        {
            return _login?.Id == id ? _login : null;
        }
    }

    public bool SubmitLoginCode(string id, string code)
    {
        LoginSession? session = GetLogin(id);
        return session != null && session.SubmitCode(code);
    }

    public bool CancelLogin(string id)
    {
        LoginSession? session = GetLogin(id);
        if (session == null) return false;
        session.Cancel();
        return true;
    }

    private void BeginLogin(LoginSession session, string username, string password)
    {
        if (session.State is "cancelled" or "failed" or "success") return;

        session.MarkStarting();
        session.Cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _lobby.StopAndReset(); // SteamKit만 로그오프, Steamworks P2P는 유지
        _ = Task.Run(() => RunLoginAsync(session, username, password));
    }

    private async Task RunLoginAsync(LoginSession session, string username, string password)
    {
        try
        {
            var authenticator = new RestAuthenticator(session);
            var result = await SteamLoginFlow.RunAsync(username, password, authenticator,
                _config.SessionPath, session.Cancellation.Token);

            session.MarkSaving();
            SteamSessionStore.Save(_config.SessionPath, result);
            session.MarkSuccess(result.AccountName);
            SteamLog.Info($"REST 로그인 성공: {result.AccountName}");
        }
        catch (OperationCanceledException)
        {
            session.Cancel();
        }
        catch (Exception ex)
        {
            session.MarkFailed(ex.Message);
            SteamLog.Info($"REST 로그인 실패: {ex.Message}");
        }
        finally
        {
            lock (_loginLock) { _restartLobbyAfterLogin = true; }
        }
    }
}
