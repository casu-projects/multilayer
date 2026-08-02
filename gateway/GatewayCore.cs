using System.Collections.Concurrent;
using System.Text.Json;

namespace CasuMpGateway;

/// <summary>게이트웨이 코어 — 세션 레지스트리 + 라우팅 미러 + 제어 명령 처리.
/// 모든 게임 데이터 경로 처리는 메인 루프 스레드에서만 일어난다.</summary>
public sealed class GatewayCore
{
    public GatewayConfig Config { get; }

    private readonly Dictionary<PlayerKey, ClientSession> _sessions = new();
    private readonly Dictionary<PlayerKey, RouteEntry> _routes = new();
    private readonly HashSet<PlayerKey> _banned = new();
    private readonly object _lock = new();

    private readonly ConcurrentQueue<ControlMessage> _inbound = new();
    private readonly ConcurrentQueue<ControlMessage> _outbound = new();
    private long _seqCounter;

    private bool _maintenance;
    private string _maintenanceMessage = "";

    public GatewayCore(GatewayConfig config)
    {
        Config = config;
        LoadBanList();
    }

    public bool IsMaintenance => _maintenance;
    public string MaintenanceMessage => _maintenanceMessage;

    /// <summary>활성 세션 수 (로비 PLRCOUNT 메타데이터용).</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>오케스트레이터 LOBBY_METADATA 명령 → Steam 어댑터 전달 (없으면 무시).</summary>
    public Action<LobbyMetadataPayload>? OnLobbyMetadata { get; set; }

    // ── 제어 채널 (ControlChannel) ──

    public void EnqueueCommand(ControlMessage msg) => _inbound.Enqueue(msg);

    public ControlMessage? TryDequeueOutbound() => _outbound.TryDequeue(out var msg) ? msg : null;

    public long NextSeq() => ++_seqCounter;

    /// <summary>보고 메시지 (best-effort, ack 불필요 — R1).</summary>
    public void Report(string type, object? payload) =>
        _outbound.Enqueue(ControlMessage.Report(NextSeq(), type, payload));

    /// <summary>실시간 로그 릴레이 (LOG — 오케스트레이터 콘솔 표시용).</summary>
    public void SendLog(string message) =>
        Report("LOG", new { source = "gateway", message });

    /// <summary>재연결 후 활성 세션 전부 재보고 (G12-R4).</summary>
    public void ReportActiveSessions()
    {
        lock (_lock)
        {
            foreach (ClientSession session in _sessions.Values)
            {
                Report("SESSION_CONNECTED", new
                {
                    playerKey = session.Player.Value,
                    steamId = session.SteamId,
                    username = session.Username,
                });
            }
        }
    }

    /// <summary>메인 루프 틱 — 제어 명령 처리 + 세션 틱.</summary>
    public void Tick()
    {
        while (_inbound.TryDequeue(out ControlMessage? msg) && msg != null)
        {
            HandleCommand(msg);
        }

        List<ClientSession> snapshot;
        lock (_lock)
        {
            snapshot = _sessions.Values.ToList();
        }
        foreach (ClientSession session in snapshot)
        {
            session.Tick();
            if (session.State == SessionState.Routing
                && session.RoutingWaitStartedAt.HasValue
                && DateTime.UtcNow - session.RoutingWaitStartedAt.Value > TimeSpan.FromSeconds(Config.RoutingWaitTimeoutSeconds))
            {
                Log.Info($"{session.Username} 라우팅 대기 타임아웃 — 거부.");
                session.Kick("Server is busy, please try again.");
                RemoveSession(session, "routing timeout");
            }
        }
    }

    // ── 세션 수용/종료 (어댑터 호출) ──

    /// <summary>어댑터가 새 세션을 전달 (G3 — ACCEPTED). 같은 플레이어의 기존 세션은 폐기 (G13 갭1).</summary>
    public void AcceptSession(ClientSession session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.Player, out ClientSession? old))
            {
                Log.Info($"{session.Username} 중복 세션 — 기존 세션 폐기 (G13).");
                _sessions.Remove(session.Player);
                old.Kick("Reconnected from a new session.");
            }
            _sessions[session.Player] = session;
        }

        Report("SESSION_CONNECTED", new
        {
            playerKey = session.Player.Value,
            steamId = session.SteamId,
            username = session.Username,
        });

        if (_maintenance)
        {
            session.Kick(_maintenanceMessage.Length > 0 ? _maintenanceMessage : "Maintenance.");
            RemoveSession(session, "maintenance");
            return;
        }
        if (_banned.Contains(session.Player))
        {
            session.Kick("You are banned from this server.");
            RemoveSession(session, "banned");
            return;
        }

        lock (_lock)
        {
            // 라우팅 결정은 오케스트레이터만 (ROUTE-ON-READY — 2026-08-02 수정):
            // 미러 라우트를 즉시 사용하면 재접속 플레이어가 스테일 라우트(예: 이전 레이어,
            // 정지된 인스턴스)로 오접속된다. 웜 인스턴스는 SESSION_CONNECTED 직후
            // 오케스트레이터의 ROUTE_UPDATE가 즉시 도착하므로 대기 비용이 없다.
            session.EnterRoutingWait();
            Log.Info($"{session.Username} 라우팅 대기 (오케스트레이터 결정).");
        }
    }

    /// <summary>백엔드 미연결 세션의 클라이언트 이탈 (어댑터 호출).</summary>
    public void CloseSession(ClientSession session, string reason)
    {
        if (session.Disposed) return;
        session.Dispose();
        RemoveSession(session, reason);
    }
    // ── 세션 이벤트 (ClientSession → 코어) ──
    public void OnSessionBackendConnected(ClientSession session)
    {
        Report("BACKEND_CONNECTED", new
        {
            playerKey = session.Player.Value,
            instanceId = session.InstanceId,
        });
    }

    public void OnSessionBackendFailed(ClientSession session, string reason)
    {
        session.Kick(reason);
        RemoveSession(session, reason);
    }

    // ── 내부 ──

    private void HandleCommand(ControlMessage msg)
    {
        bool ok = true;
        string? reason = null;
        try
        {
            switch (msg.Type)
            {
                case "TABLE_SNAPSHOT":
                    ApplyTableSnapshot(msg);
                    break;
                case "ROUTE_UPDATE":
                    ApplyRouteUpdate(msg);
                    break;
                case "SWAP":
                    ApplySwap(msg);
                    break;
                case "KICK":
                    ApplyKick(msg);
                    break;
                case "BAN":
                    ApplyBan(msg);
                    break;
                case "MAINTENANCE":
                    ApplyMaintenance(msg);
                    break;
                case "LOBBY_METADATA":
                    ApplyLobbyMetadata(msg);
                    break;
                default:
                    ok = false;
                    reason = $"unknown type: {msg.Type}";
                    break;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            reason = ex.Message;
            Log.Info($"명령 처리 실패 ({msg.Type}): {ex}");
        }

        _outbound.Enqueue(ControlMessage.Ack(msg.Seq, ok, reason));
    }

    private void ApplyTableSnapshot(ControlMessage msg)
    {
        var entries = msg.PayloadAs<TableSnapshot>() ?? throw new InvalidDataException("payload 없음");
        lock (_lock)
        {
            _routes.Clear();
            foreach (RouteEntry entry in entries.Entries)
            {
                _routes[PlayerKey.FromString(entry.PlayerKey)] = entry;
            }
        }
        Log.Info($"라우팅 테이블 스냅샷 적용: {entries.Entries.Length}개");
    }

    private void ApplyRouteUpdate(ControlMessage msg)
    {
        var entry = msg.PayloadAs<RouteEntry>() ?? throw new InvalidDataException("payload 없음");
        lock (_lock)
        {
            _routes[PlayerKey.FromString(entry.PlayerKey)] = entry;
            // Routing 대기 세션 + 아직 백엔드에 한 번도 연결 성공하지 못한 Connecting 세션을
            // 재라우팅한다 (죽은 주소로 재시도 중인 세션이 새 인스턴스 주소로 전환 — 방어 2).
            if (_sessions.TryGetValue(PlayerKey.FromString(entry.PlayerKey), out ClientSession? session)
                && IsUsableBackendAddr(entry.BackendAddr)
                && (session.State == SessionState.Routing
                    || (session.State == SessionState.Connecting && !session.HasEverConnectedToBackend)))
            {
                session.BeginRoute(entry.BackendAddr, entry.ClientId, entry.IsReturning, entry.InstanceId);
            }
        }
    }

    private void ApplySwap(ControlMessage msg)
    {
        var payload = msg.PayloadAs<SwapPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);

        ClientSession? session;
        lock (_lock)
        {
            // 라우트 테이블도 함께 갱신 — SWAP 후 재접속 시 스테일 라우트(이전 인스턴스)로
            // 즉시 연결되는 것 방지 (스테일 라우트로 연결 성공하면 ROUTE_UPDATE 재라우팅이
            // 무효화되어 이전 레이어에 고정됨). 세션 유무와 무관하게 먼저 갱신.
            if (_routes.TryGetValue(key, out RouteEntry? existing))
            {
                _routes[key] = new RouteEntry
                {
                    PlayerKey = existing.PlayerKey,
                    InstanceId = payload.InstanceId,
                    ClientId = existing.ClientId,
                    BackendAddr = payload.BackendAddr,
                    IsReturning = existing.IsReturning,
                };
            }

            if (!_sessions.TryGetValue(key, out session))
            {
                // 활성 세션 없음 — 마이그레이션 대상이 이미 접속 종료. 오케스트레이터가 상태 정리.
                Log.Info($"{payload.PlayerKey} SWAP 명령 — 활성 세션 없음.");
                return;
            }
        }
        Log.Info($"{session.Username} 세션을 {payload.BackendAddr}(으)로 전환 (클라 연결 유지).");
        // SWAP 멱등성 (2026-08-02): 이미 같은 목적지로 Active/Connecting/Swapping이면
        // 재연결하지 않는다 — 중복 SWAP(오케스트레이터 재전송/재시도)으로 백엔드를 이중
        // 연결하면 인스턴스의 "Player with this name already exists" 추방이 발생한다.
        if (session.BackendAddr == payload.BackendAddr
            && session.State is SessionState.Active or SessionState.Connecting or SessionState.Swapping)
        {
            Log.Info($"{session.Username} 이미 {payload.BackendAddr} 대상 — SWAP 스킵 (멱등).");
            return;
        }
        session.SwapBackend(payload.BackendAddr, payload.InstanceId);
    }

    private void ApplyKick(ControlMessage msg)
    {
        var payload = msg.PayloadAs<KickPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);
        lock (_lock)
        {
            if (_sessions.TryGetValue(key, out ClientSession? session))
            {
                session.Kick(payload.Reason ?? "Kicked.");
                RemoveSession(session, "kick");
            }
        }
    }

    private void ApplyBan(ControlMessage msg)
    {
        var payload = msg.PayloadAs<BanPayload>() ?? throw new InvalidDataException("payload 없음");
        PlayerKey key = PlayerKey.FromString(payload.PlayerKey);
        lock (_lock)
        {
            if (payload.Banned)
            {
                _banned.Add(key);
                if (_sessions.TryGetValue(key, out ClientSession? session))
                {
                    session.Kick("You are banned from this server.");
                    RemoveSession(session, "banned");
                }
            }
            else
            {
                _banned.Remove(key);
            }
        }
        SaveBanList();
    }

    private void ApplyMaintenance(ControlMessage msg)
    {
        var payload = msg.PayloadAs<MaintenancePayload>() ?? throw new InvalidDataException("payload 없음");
        _maintenance = payload.On;
        _maintenanceMessage = payload.Message ?? "";
        Log.Info($"유지보수 모드 {(payload.On ? "켬" : "끔")}.");
    }

    private void ApplyLobbyMetadata(ControlMessage msg)
    {
        var payload = msg.PayloadAs<LobbyMetadataPayload>() ?? throw new InvalidDataException("payload 없음");
        OnLobbyMetadata?.Invoke(payload);
    }

    private void RemoveSession(ClientSession session, string reason)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.Remove(session.Player);
        }
        if (removed)
        {
            Report("SESSION_DISCONNECTED", new
            {
                playerKey = session.Player.Value,
                reason,
            });
        }
    }

    private void LoadBanList()
    {
        try
        {
            if (!File.Exists(Config.BanListPath)) return;
            var list = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Config.BanListPath)) ?? [];
            foreach (string key in list)
            {
                _banned.Add(PlayerKey.FromString(key));
            }
        }
        catch (Exception ex)
        {
            Log.Info($"밴 목록 로드 실패: {ex.Message}");
        }
    }

    private void SaveBanList()
    {
        try
        {
            string[] list;
            lock (_lock)
            {
                list = _banned.Select(b => b.Value).ToArray();
            }
            File.WriteAllText(Config.BanListPath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Log.Info($"밴 목록 저장 실패: {ex.Message}");
        }
    }

    /// <summary>백엔드 연결 가능한 주소인지 — 빈/무효 주소는 스테일 라우트로 취급한다.</summary>
    internal static bool IsUsableBackendAddr(string? addr) =>
        !string.IsNullOrEmpty(addr) && addr.Contains(':');

    // ── payload 모델 (G12-R1) ──

    public sealed class TableSnapshot
    {
        public RouteEntry[] Entries { get; set; } = [];
    }

    public sealed class SwapPayload
    {
        public string PlayerKey { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string BackendAddr { get; set; } = "";
    }

    public sealed class KickPayload
    {
        public string PlayerKey { get; set; } = "";
        public string? Reason { get; set; }
    }

    public sealed class BanPayload
    {
        public string PlayerKey { get; set; } = "";
        public bool Banned { get; set; }
    }

    public sealed class MaintenancePayload
    {
        public bool On { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>오케스트레이터가 인스턴스 리포트를 합산해 보내는 로비 동적 메타데이터 (G2).</summary>
    public sealed class LobbyMetadataPayload
    {
        public int LivingCount { get; set; }
        public int HappinessSum { get; set; }
        public ulong[]? SteamIds { get; set; }
        public string? RulesBase64 { get; set; }
        public string[]? ModListGuids { get; set; }
        public bool EnforceModList { get; set; }
    }
}
