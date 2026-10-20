using CasuMp.Protocol;
using System.Text.Json;

namespace CasuMpServer;

public sealed class MigrationCoordinator
{
    private int MaxLayers => _config.LayerCount;

    private enum TxState
    {
        Idle,
        Freezing,
        WaitingReady,   // P1: 목적지 READY 대기 - READY 확인 후에만 SWAP 발행
        Swapping,
        Resuming,
        Worldgen,
    }

    private sealed class Transaction
    {
        public required PlayerKey Player { get; init; }
        public required int Epoch { get; init; }
        public required int FromDepth { get; init; }
        public required int ToDepth { get; init; }
        public required string FromInstance { get; init; }
        public string? TargetInstance { get; set; }
        public TxState State { get; set; } = TxState.Freezing;
        public DateTime StepDeadline { get; set; }
        public int ResumeRetries { get; set; }
        public bool SwapSent { get; set; }
        public bool PreClearSent { get; set; }
        public bool PreClearDone { get; set; }
        public bool Recovered { get; set; }
        public bool ResumeSent { get; set; }
        public bool IsRespawn { get; set; }
    }

    private sealed class WalEntry
    {
        public int Epoch { get; set; }
        public string State { get; set; } = "";
        public int FromDepth { get; set; }
        public int ToDepth { get; set; }
        public string FromInstance { get; set; } = "";
        public string? TargetInstance { get; set; }
        public bool IsRespawn { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    private readonly ServerConfig _config;
    private readonly ControlHub _hub;
    private readonly ClientGateway _gateway;
    private readonly InstanceManager _instances;
    private readonly PlayerSessionStore _sessions;
    private readonly PlayerDataStore _dataStore;

    private readonly Dictionary<PlayerKey, Transaction> _transactions = new();
    private readonly Dictionary<PlayerKey, WalEntry> _wal = new();
    private int _lastEpoch;

    public event Action<PlayerKey, int, int, bool>? MigrationCommitted;

    public MigrationCoordinator(ServerConfig config, ControlHub hub, ClientGateway gateway,
        InstanceManager instances, PlayerSessionStore sessions, PlayerDataStore dataStore)
    {
        _config = config;
        _hub = hub;
        _gateway = gateway;
        _instances = instances;
        _sessions = sessions;
        _dataStore = dataStore;
        LoadWalAndRecover();
    }

    public bool IsMigrating(PlayerKey key) => _transactions.ContainsKey(key);

    public IReadOnlyCollection<PlayerKey> ActiveMigrations => _transactions.Keys.ToList();

    public int CountTargeting(string instanceKey) =>
        _transactions.Values.Count(t => t.TargetInstance == instanceKey);

    private string WalPath => Path.Combine(_config.SaveRootPath, "migration-wal.json");

    private void LoadWalAndRecover()
    {
        try
        {
            if (!File.Exists(WalPath)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, WalEntry>>(File.ReadAllText(WalPath));
            if (raw == null) return;

            foreach ((string key, WalEntry entry) in raw)
            {
                var player = PlayerKey.FromString(key);
                _lastEpoch = Math.Max(_lastEpoch, entry.Epoch);
                _wal[player] = entry;

                var tx = new Transaction
                {
                    Player = player,
                    Epoch = entry.Epoch,
                    FromDepth = entry.FromDepth,
                    ToDepth = entry.ToDepth,
                    FromInstance = entry.FromInstance,
                    TargetInstance = entry.TargetInstance,
                    Recovered = true,
                    IsRespawn = entry.IsRespawn,
                };

                switch (entry.State)
                {
                    case "WaitingReady":
                        tx.State = TxState.WaitingReady;
                        break;
                    case "Swapping":
                        tx.State = TxState.Resuming; // SWAP ack 후 = 게이트웨이가 명령 보유, RESUME 재발신
                        tx.ResumeSent = false;
                        MarkSessionAtTarget(tx);
                        break;
                    case "Resuming":
                    case "Worldgen":
        tx.State = TxState.Worldgen; // 목적지가 데이터 보유 -> 월드젠 완료 대기
                        MarkSessionAtTarget(tx); // 재시작 복구: 목적지로 전진
                        break;
                    case "Freezing":
                    default:
                        tx.State = TxState.Freezing; // FREEZE_DONE 대기/타임아웃 롤백
                        break;
                }

                tx.StepDeadline = DateTime.UtcNow + StepTimeout();
                _transactions[player] = tx;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"WAL 로드 실패: {ex.Message}");
        }
    }

    private void MarkSessionAtTarget(Transaction tx)
    {
        PlayerState? state = _sessions.Get(tx.Player);
        if (state != null)
        {
            state.Depth = tx.ToDepth;
            state.InstanceId = tx.TargetInstance;
            state.IsReturning = true;
            _sessions.Persist(state);
        }
    }

    private void WriteWal(Transaction tx)
    {
        _wal[tx.Player] = new WalEntry
        {
            Epoch = tx.Epoch,
            State = WalStateName(tx.State),
            FromDepth = tx.FromDepth,
            ToDepth = tx.ToDepth,
            FromInstance = tx.FromInstance,
            TargetInstance = tx.TargetInstance,
            IsRespawn = tx.IsRespawn,
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        };
        PersistWal();
    }

    private void RemoveWal(PlayerKey key)
    {
        if (_wal.Remove(key)) PersistWal();
    }

    private void PersistWal()
    {
        try
        {
            var flat = _wal.ToDictionary(kv => kv.Key.Value, kv => kv.Value);
            AtomicJsonFile.Write(WalPath, JsonSerializer.Serialize(flat));
        }
        catch (Exception ex)
        {
            Log.Info($"WAL 저장 실패: {ex.Message}");
        }
    }

    private static string WalStateName(TxState state) => state switch
    {
        TxState.WaitingReady => "WaitingReady",
        TxState.Swapping => "Swapping",
        TxState.Resuming => "Resuming",
        TxState.Worldgen => "Worldgen",
        _ => "Freezing",
    };

    public void OnLayerEnd(PlayerKey player, int fromDepth, int maxLayers)
    {
        if (_transactions.ContainsKey(player))
        {
            return;
        }

        PlayerState? state = _sessions.Get(player);
        if (state == null)
        {
            return;
        }
        if (state.Session == PlayerSessionState.Offline)
        {
            return;
        }
        if (fromDepth != state.Depth)
        {
            Log.Info($"{player} LAYER_END 레이어 불일치 — 보고 {fromDepth}, 세션 {state.Depth} (무시).");
            return;
        }

        int toDepth = fromDepth < maxLayers ? fromDepth + 1 : 1;
        BeginMigration(player, fromDepth, toDepth);
    }

    public int NextLayerDepth(int fromDepth) =>
        fromDepth < MaxLayers ? fromDepth + 1 : 1;

    public string? ManualMigrate(PlayerKey player, int toDepth)
    {
        PlayerState? state = _sessions.Get(player);
        if (state == null || state.Session == PlayerSessionState.Offline)
        {
            return "플레이어가 접속 중이 아닙니다.";
        }
        if (_transactions.ContainsKey(player))
        {
            return "이미 마이그레이션 중입니다.";
        }
        if (state.Depth == toDepth)
        {
            return $"이미 L{toDepth}에 있습니다.";
        }
        if (toDepth < 1 || toDepth > MaxLayers)
        {
            return $"유효하지 않은 목적지 레이어 — 1~{MaxLayers} 범위여야 합니다.";
        }
        InstanceInfo? fromInstance = _instances.FindByDepth(state.Depth);
        if (fromInstance == null
            || fromInstance.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
        {
            return $"출발 인스턴스(depth-{state.Depth})가 준비되지 않았습니다.";
        }

        BeginMigration(player, state.Depth, toDepth);
        return null;
    }

    private void BeginMigration(PlayerKey player, int fromDepth, int toDepth)
    {
        InstanceInfo? fromInstance = _instances.FindByDepth(fromDepth);
        if (fromInstance == null)
        {
            return;
        }

        string? targetAddr = _instances.EnsureInstance(toDepth); // 없으면 스폰/부팅
        if (targetAddr == null)
        {
            Log.Info($"{player} 목적지 인스턴스 확보 실패 — 중단.");
            return;
        }

        int epoch = ++_lastEpoch;
        var tx = new Transaction
        {
            Player = player,
            Epoch = epoch,
            FromDepth = fromDepth,
            ToDepth = toDepth,
            FromInstance = fromInstance.Key,
            TargetInstance = _instances.FindByDepth(toDepth)?.Key,
        };
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();
        _transactions[player] = tx;
        _sessions.Get(player)!.Session = PlayerSessionState.Migrating; // 라우팅 잠금
        WriteWal(tx);

        _hub.Send(fromInstance.ModConnection, MessageTypes.Freeze, // 출발지 동결 + 데이터 캡처
            new { playerKey = player.Value, epoch }, (ok, reason) =>
            {
                if (!ok) Abort(tx, $"FREEZE 전송 실패: {reason}");
            });
    }

    public void OnRespawnRequest(PlayerKey player, int fromDepth)
    {
        if (_transactions.ContainsKey(player))
        {
            return;
        }
        PlayerState? state = _sessions.Get(player);
        if (state == null)
        {
            return;
        }
        if (state.Session == PlayerSessionState.Offline)
        {
            return;
        }

        _dataStore.DeleteSave(player); // 리스폰 = 완전 신규

        if (fromDepth <= 1)
        {
            _sessions.ResetToFresh(player, keepOnline: true); // L1은 인플레이스 리스폰
            return;
        }

        _sessions.ResetToFresh(player);
        InstanceInfo? fromInstance = _instances.FindByDepth(fromDepth);
        if (fromInstance == null)
        {
            Log.Info($"{player} 리스폰 — 출발 인스턴스 없음 (depth {fromDepth}) — 중단.");
            return;
        }
        string? targetAddr = _instances.EnsureInstance(1); // L1 목적지
        if (targetAddr == null)
        {
            Log.Info($"{player} 리스폰 — 목적지 인스턴스 확보 실패 — 중단.");
            return;
        }

        int epoch = ++_lastEpoch;
        var tx = new Transaction
        {
            Player = player,
            Epoch = epoch,
            FromDepth = fromDepth,
            ToDepth = 1,
            FromInstance = fromInstance.Key,
            TargetInstance = _instances.FindByDepth(1)?.Key,
            IsRespawn = true,
        };
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();
        _transactions[player] = tx;
        state.Session = PlayerSessionState.Migrating;
        WriteWal(tx);

        _hub.Send(fromInstance.ModConnection, MessageTypes.Freeze, // 리스폰: 캡처 스킵 + 목적지 프레시 생성
            new { playerKey = player.Value, epoch, respawn = true }, (ok, reason) =>
            {
                if (!ok) Abort(tx, $"FREEZE 전송 실패: {reason}");
            });
    }

    public void OnFreezeDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx) || tx.State != TxState.Freezing) return;
        if (!IsCurrent(tx, epoch)) return;

        tx.State = TxState.WaitingReady; // 목적지 READY 게이트
        tx.StepDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(_config.MigrationReadyWaitTimeoutSeconds);
        WriteWal(tx);

        TryProceedSwap(tx);
    }

    public void OnPreClearDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx) || tx.State != TxState.WaitingReady) return;
        if (!IsCurrent(tx, epoch)) return;

        tx.PreClearDone = true; // 출발지 엔티티 정리 완료
        TryProceedSwap(tx);
    }

    private void TryProceedSwap(Transaction tx)
    {
        if (tx.State != TxState.WaitingReady) return;
        if (tx.SwapSent) return;

        InstanceInfo? target = _instances.Find(tx.TargetInstance ?? "");
        if (target == null || target.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            _instances.EnsureInstance(tx.ToDepth);
            tx.TargetInstance = _instances.FindByDepth(tx.ToDepth)?.Key;
            return;
        }
        if (target.Status is InstanceStatus.Suspended or InstanceStatus.Suspending)
        {
            _instances.ResumeInstance(target.Key);
            return;
        }
        if (target.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
        {
            return; // 부팅/월드젠/재개 중 - Tick 재시도
        }

        string? addr = _instances.BackendAddrFor(target);
        if (addr == null) return;

        if (!tx.PreClearDone) // SWAP 직전: 대상 클라 스테일 엔티티 정리 핸드셰이크
        {
            if (!tx.PreClearSent)
            {
                InstanceInfo? from = _instances.Find(tx.FromInstance);
                if (from?.ModConnection != null && !from.ModConnection.Closed)
                {
                    tx.PreClearSent = true; // 미응답 시 best-effort로 SWAP 진행
                    tx.StepDeadline = DateTime.UtcNow + PreClearWaitTimeout();
                    Log.Info($"{tx.Player} → PRE-CLEAR 발신 (출발지 {tx.FromInstance})");
                    _hub.Send(from.ModConnection, MessageTypes.PreClear,
                        new { playerKey = tx.Player.Value, epoch = tx.Epoch },
                        (ok, reason) =>
                        {
                            if (!ok)
                            {
                                Log.Info($"{tx.Player} PRE-CLEAR 전송 실패({reason}) - 생략하고 진행.");
                                tx.PreClearDone = true;
                                TryProceedSwap(tx);
                            }
                        });
                }
                else
                {
                    tx.PreClearDone = true; // 출발지 연결 없음 - 그냥 진행
                }
            }
            if (!tx.PreClearDone) return; // PRECLEAR_DONE 대기 (이벤트/Tick 재시도)
        }

        tx.SwapSent = true;
        if (!_gateway.Swap(tx.Player, addr, tx.TargetInstance)) // 클라 연결 유지, 백엔드만 교체
        {
            Abort(tx, "SWAP 실패: 세션 없음");
            return;
        }
        tx.State = TxState.Swapping;
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();
        WriteWal(tx);
    }

    public void OnBackendConnected(PlayerKey player, string instanceId, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (tx.State != TxState.Swapping) return;
        if (!IsCurrent(tx, epoch)) return;
        if (tx.TargetInstance != null && instanceId != tx.TargetInstance)
        {
            Abort(tx, $"백엔드가 목적지가 아닌 {instanceId}에 연결됨"); // 출발지로 복귀 = 스왑 실패
            return;
        }

        tx.State = TxState.Resuming;
        tx.ResumeSent = true;
        tx.ResumeRetries = 0;
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();

        SendResume(tx);
    }

    private void SendResume(Transaction tx)
    {
        ControlHub.ClientConnection? targetConn = _hub.ModConnection(tx.TargetInstance ?? "");
        if (targetConn == null)
        {
            return;
        }

        JsonElement? payload = _dataStore.GetForMigration(tx.Player); // 마이그레이션 데이터
        ushort[] ghostClientIds = _sessions.All // 출발지 잔류자 정리용, 동시 도착자는 제외
            .Where(s => s.InstanceId == tx.FromInstance
                && s.Session != PlayerSessionState.Offline
                && s.Key != tx.Player
                && !MigratingToSameTarget(tx, s.Key))
            .Select(s => s.ClientId)
            .ToArray();

        if (tx.IsRespawn)
        {
            _hub.Send(targetConn, MessageTypes.Resume,
                new { playerKey = tx.Player.Value, epoch = tx.Epoch, respawn = true, ghostClientIds = ghostClientIds },
                (ok, reason) =>
                {
                    if (!ok) { Abort(tx, $"RESUME 전송 실패: {reason}"); return; }
                    if (tx.State != TxState.Resuming) return;
                    WriteWal(tx);
                });
        }
        else
        {
            _hub.Send(targetConn, MessageTypes.Resume,
                new { playerKey = tx.Player.Value, epoch = tx.Epoch, payload = payload, ghostClientIds = ghostClientIds },
                (ok, reason) =>
                {
                    if (!ok) { Abort(tx, $"RESUME 전송 실패: {reason}"); return; }
                    if (tx.State != TxState.Resuming) return;
                    WriteWal(tx); // WAL "Resuming" = 목적지가 데이터 보유
                });
        }
    }

    public void OnResumeDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx) || tx.State != TxState.Resuming) return;
        if (!IsCurrent(tx, epoch)) return;
        tx.State = TxState.Worldgen;
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();
        WriteWal(tx);
    }

    public void OnWorldgenDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (!IsCurrent(tx, epoch)) return;

        _sessions.CommitMigration(player, tx.ToDepth, tx.TargetInstance); // 라우팅 확정
        _dataStore.CommitPending(player); // 보류분 확정
        if (tx.IsRespawn)
        {
            _dataStore.DeleteSave(player); // 리스폰 세이브 폐기
        }
        _transactions.Remove(player);
        RemoveWal(player);

        InstanceInfo? fromInstance = _instances.Find(tx.FromInstance);
        if (fromInstance != null)
        {
            _hub.Send(fromInstance.ModConnection, MessageTypes.Release, // 양쪽 동결 해제
                new { playerKey = player.Value, epoch = tx.Epoch });
        }
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, MessageTypes.Release,
                new { playerKey = player.Value, epoch = tx.Epoch });
        }

        MigrationCommitted?.Invoke(player, tx.FromDepth, tx.ToDepth, tx.IsRespawn);
    }

    public void OnSwapFailed(PlayerKey player, string reason)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        Abort(tx, $"SWAP_FAILED: {reason}");
    }

    public void OnPlayerLeftDuringMigration(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (tx.State != TxState.Freezing) return;
        if (!IsCurrent(tx, epoch)) return;

        EndMigrationOnLeave(tx);
    }

    public void OnPlayerQuitDuringMigration(PlayerKey player)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        EndMigrationOnLeave(tx);
    }

    private void EndMigrationOnLeave(Transaction tx)
    {
        if (!_transactions.Remove(tx.Player)) return;
        RemoveWal(tx.Player);

        _dataStore.CommitPending(tx.Player);

        PlayerState? state = _sessions.Get(tx.Player);
        if (state != null)
        {
            state.Depth = tx.ToDepth;
            state.Session = PlayerSessionState.Offline;
            state.IsReturning = true;
            _sessions.Persist(state);
        }

        InstanceInfo? fromInstance = _instances.Find(tx.FromInstance);
        if (fromInstance != null)
        {
            _hub.Send(fromInstance.ModConnection, MessageTypes.Release,
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, MessageTypes.Release,
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }

    }

    public void OnInstanceReady(string instanceKey)
    {
        foreach (Transaction tx in _transactions.Values.Where(t =>
            t.State == TxState.WaitingReady && t.TargetInstance == instanceKey))
        {
            TryProceedSwap(tx);
        }
    }

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Transaction tx in _transactions.Values.ToList())
        {
            if (tx.State == TxState.WaitingReady)
            {
                if (tx.SwapSent)
                {
                    InstanceInfo? target = _instances.Find(tx.TargetInstance ?? "");
                    if (target == null || target.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
                    {
                        _instances.EnsureInstance(tx.ToDepth); // 인플라이트 중 목적지 사망 -> 재스폰
                        tx.TargetInstance = _instances.FindByDepth(tx.ToDepth)?.Key;
                        tx.SwapSent = false;
                    }
                }
                else
                {
                    if (tx.PreClearSent && !tx.PreClearDone && now >= tx.StepDeadline)
                    {
                        tx.PreClearDone = true; // PRECLEAR 미응답 -> best-effort
                        tx.StepDeadline = now + StepTimeout();
                    }
                    TryProceedSwap(tx);
                }
            }

            if (tx.State == TxState.Resuming && !tx.ResumeSent) // 복구: 목적지 모드 연결 시 재발신
            {
                if (_hub.ModConnection(tx.TargetInstance ?? "") != null)
                {
                    tx.ResumeSent = true;
                    SendResume(tx);
                }
            }

            if (now < tx.StepDeadline) continue;

            if (tx.State == TxState.Resuming && tx.ResumeRetries < 2) // RESUME 2회까지 재시도
            {
                tx.ResumeRetries++;
                tx.StepDeadline = now + StepTimeout();
                SendResume(tx);
                continue;
            }
            Abort(tx, $"단계 타임아웃 ({tx.State})");
        }
    }

    private void Abort(Transaction tx, string reason)
    {
        if (!_transactions.Remove(tx.Player)) return;
        RemoveWal(tx.Player);
        Log.Info($"{tx.Player} 중단: {reason} — 롤백.");

        bool postSwap = tx.State is TxState.Resuming or TxState.Worldgen; // 스왑 이후 실패 여부

        PlayerState? state = _sessions.Get(tx.Player);
        if (state != null)
        {
            state.Session = PlayerSessionState.OnLayer; // 실패해도 목적지로 배치 -> 재접속 유도
            state.InstanceId = tx.TargetInstance;
            state.Depth = tx.ToDepth;
            _sessions.Persist(state);
        }
        _dataStore.CommitPending(tx.Player);

        InstanceInfo? fromInstance = _instances.Find(tx.FromInstance);
        if (fromInstance != null)
        {
            if (!postSwap)
            {
                JsonElement? payload = tx.IsRespawn ? null : _dataStore.GetForMigration(tx.Player); // 인벤토리 복원용
                _hub.Send(fromInstance.ModConnection, MessageTypes.Unfreeze,
                    new { playerKey = tx.Player.Value, epoch = tx.Epoch, payload = payload });
            }
            else
            {
                _hub.Send(fromInstance.ModConnection, MessageTypes.Unfreeze,
                    new { playerKey = tx.Player.Value, epoch = tx.Epoch });
            }
        }
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, MessageTypes.Unfreeze,
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }

        string? kickInstance = postSwap ? tx.TargetInstance : tx.FromInstance;
        if (kickInstance != null)
        {
            _hub.Send(_hub.ModConnection(kickInstance), MessageTypes.KickPlayer, // 실패 추방 -> 재접속 복구 경로
                new { playerKey = tx.Player.Value, reason = "Migration Failed. Please reconnect." });
        }
    }

    private static bool IsCurrent(Transaction tx, int? epoch) =>
        !epoch.HasValue || epoch.Value == tx.Epoch;

    private bool MigratingToSameTarget(Transaction tx, PlayerKey other) =>
        _transactions.TryGetValue(other, out Transaction? t) && t.TargetInstance == tx.TargetInstance;

    private TimeSpan StepTimeout() => TimeSpan.FromSeconds(_config.MigrationStepTimeoutSeconds);

    private static TimeSpan PreClearWaitTimeout() => TimeSpan.FromSeconds(4);
}
