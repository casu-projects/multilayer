using System.Text.Json;

namespace CasuMpOrchestrator;

// 마이그레이션 트랜잭션 - FREEZE -> READY 대기 -> SWAP -> RESUME -> WORLDGEN -> COMMIT
// 단일 기록자 규칙 enforcement + 단계별 타임아웃/롤백 + epoch 멱등 + WAL 영속
// 모든 접근은 메인 스레드 전용
public sealed class MigrationCoordinator
{
    // 게임 레이어 수 - 수동 마이그레이션(`migrate`)의 "다음 레이어" 계산/목적지 범위 검증용
    private const int MaxLayers = 5;

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
        // SWAP 인플라이트: 발신~ack 수신 사이 Tick이 SWAP을 재발신해
        // 게이트웨이가 백엔드를 이중 연결("이름 중복" 추방)하는 것을 차단한다
        public bool SwapSent { get; set; }
        // WAL 복구된 트랜잭션 - 스왑 후 상태면 RESUME 재발신이 필요할 수 있다
        public bool Recovered { get; set; }
        // 복구 후 RESUME 재발신 완료 여부
        public bool ResumeSent { get; set; }
        // 리스폰 트랜잭션 - FREEZE 캡처 스킵 + RESUME payload null + COMMIT 시 세이브 폐기
        // WAL에 영속되어 크래시 복구 후에도 프레시 신규 의미를 유지한다
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

    private readonly OrchestratorConfig _config;
    private readonly ControlHub _hub;
    private readonly InstanceManager _instances;
    private readonly PlayerSessionStore _sessions;
    private readonly PlayerDataStore _dataStore;

    private readonly Dictionary<PlayerKey, Transaction> _transactions = new();
    private readonly Dictionary<PlayerKey, WalEntry> _wal = new();
    private int _lastEpoch;

    // 마이그레이션 커밋 완료 이벤트 (player, fromDepth, toDepth) - Discord 알림 등 외부 구독용
    public event Action<PlayerKey, int, int>? MigrationCommitted;

    public MigrationCoordinator(OrchestratorConfig config, ControlHub hub,
        InstanceManager instances, PlayerSessionStore sessions, PlayerDataStore dataStore)
    {
        _config = config;
        _hub = hub;
        _instances = instances;
        _sessions = sessions;
        _dataStore = dataStore;
        LoadWalAndRecover();
    }

    public bool IsMigrating(PlayerKey key) => _transactions.ContainsKey(key);

    public IReadOnlyCollection<PlayerKey> ActiveMigrations => _transactions.Keys.ToList();

    // 이 인스턴스를 목적지로 하는 진행 중 마이그레이션 수 (유휴 정리 계수용
    // 마이그레이션 도착 대기 중인 목적지를 유휴로 오판정해 강제 정지하지 않도록 한다)
    public int CountTargeting(string instanceKey) =>
        _transactions.Values.Count(t => t.TargetInstance == instanceKey);

    // WAL

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
                        // SWAP ack 수령 상태 = 게이트웨이가 명령을 보유 - 목적지로 전진 복구
                        // RESUME은 목적지 모드 연결 시 Tick에서 재발신한다 (멱등)
                        tx.State = TxState.Resuming;
                        tx.ResumeSent = false;
                        MarkSessionAtTarget(tx);
                        break;
                    case "Resuming":
                    case "Worldgen":
                        tx.State = TxState.Worldgen;
                        MarkSessionAtTarget(tx);
                        break;
                    case "Freezing":
                    default:
                        // FREEZE_DONE 대기 - 자연 타임아웃/UNFREEZE 롤백 경로로 수렴
                        tx.State = TxState.Freezing;
                        break;
                }

                tx.StepDeadline = DateTime.UtcNow + StepTimeout();
                _transactions[player] = tx;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WAL 로드 실패: {ex.Message}");
        }
    }

    // 복구 시 스왑 후 트랜잭션은 세션을 목적지로 고정 (재접속 라우팅 정합)
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
            // System.Text.Json은 struct를 딕셔너리 키로 직렬화할 수 없다 (실측:
            // PlayerKey 키로 기록하면 WAL이 조용히 실패) - string 키로 기록한다
            var flat = _wal.ToDictionary(kv => kv.Key.Value, kv => kv.Value);
            string dir = _config.SaveRootPath;
            Directory.CreateDirectory(dir);
            string tmp = WalPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(flat));
            File.Move(tmp, WalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WAL 저장 실패: {ex.Message}");
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

    // 진입: LAYER_END (모드 A) / 수동 마이그레이션 (콘솔)

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
        // 보고된 fromDepth와 세션 레이어가 다르면 스테일 보고 (UNFREEZE 복원 후 재발화 등)로
        // 간주하고 무시 - 잘못된 기준의 재마이그레이션 차단.
        if (fromDepth != state.Depth)
        {
            Console.WriteLine($"{player} LAYER_END 레이어 불일치 — 보고 {fromDepth}, 세션 {state.Depth} (무시).");
            return;
        }

        int toDepth = fromDepth < maxLayers ? fromDepth + 1 : 1;
        BeginMigration(player, fromDepth, toDepth);
    }

    // 다음 레이어 계산 - MaxLayers 초과 시 1로 래핑 (LAYER_END와 동일 규칙,
    // 수동 마이그레이션의 목적지 생략 시 사용)
    public int NextLayerDepth(int fromDepth) =>
        fromDepth < MaxLayers ? fromDepth + 1 : 1;

    // 수동 마이그레이션 (운영자 콘솔 `migrate <player> [targetLayer]`)
    // LAYER_END와 동일한 전체 트랜잭션 (FREEZE -> 목적지 월드젠 -> SWAP -> RESUME -> COMMIT)
    // 실패 시 콘솔 피드백용 사유 문자열을 반환한다 (null = 성공)
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

    // 마이그레이션 트랜잭션 공통 시작부 - FREEZE 발신 + WAL 기록
    // 호출부가 fromDepth/toDepth를 결정한다 (LAYER_END / 수동 migrate)
    private void BeginMigration(PlayerKey player, int fromDepth, int toDepth)
    {
        InstanceInfo? fromInstance = _instances.FindByDepth(fromDepth);
        if (fromInstance == null)
        {
            return;
        }

        string? targetAddr = _instances.EnsureInstance(toDepth);
        if (targetAddr == null)
        {
            Console.WriteLine($"{player} 목적지 인스턴스 확보 실패 — 중단.");
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
        _sessions.Get(player)!.Session = PlayerSessionState.Migrating;
        WriteWal(tx);

        _hub.Send(fromInstance.ModConnection, "FREEZE",
            new { playerKey = player.Value, epoch }, (ok, reason) =>
            {
                if (!ok) Abort(tx, $"FREEZE 전송 실패: {reason}");
            });
    }

    // 진입: RESPAWN (!respawn - )

    // 리스폰 처리 - 완전 신규 취급 (세이브 폐기 + 세션 프레시화)
    // fromDepth 1: 인플레이스 무로딩 리스폰 - 세이브 폐기 + 세션 프레시(온라인 유지)만,
    // 트랜잭션 없음 (모드가 로컬 리셋을 즉시 수행 - 오케스트레이터는 데이터 계층만 담당)
    // fromDepth N: 레이어 1 하향 마이그레이션 트랜잭션 (IsRespawn 플래그) - FREEZE 캡처
    // 스킵 + RESUME payload null -> 목적지에서 프레시 생성 + 보급품 지급
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

        // 리스폰 = 완전 신규 - 옛 세이브/보류분 폐기 (단일 소유자 규칙)
        _dataStore.DeleteSave(player);

        if (fromDepth <= 1)
        {
            // Case B - 인플레이스 (레이어 1): 세션만 프레시 표기. 라우팅/접속 상태 보존
            _sessions.ResetToFresh(player, keepOnline: true);
            return;
        }

        // Case A - 하향 마이그레이션 (레이어 1)
        _sessions.ResetToFresh(player);
        InstanceInfo? fromInstance = _instances.FindByDepth(fromDepth);
        if (fromInstance == null)
        {
            Console.WriteLine($"{player} 리스폰 — 출발 인스턴스 없음 (depth {fromDepth}) — 중단.");
            return;
        }
        string? targetAddr = _instances.EnsureInstance(1);
        if (targetAddr == null)
        {
            Console.WriteLine($"{player} 리스폰 — 목적지 인스턴스 확보 실패 — 중단.");
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

        _hub.Send(fromInstance.ModConnection, "FREEZE",
            new { playerKey = player.Value, epoch, respawn = true }, (ok, reason) =>
            {
                if (!ok) Abort(tx, $"FREEZE 전송 실패: {reason}");
            });
    }

    // 진행 이벤트

    public void OnFreezeDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx) || tx.State != TxState.Freezing) return;
        if (!IsCurrent(tx, epoch)) return;

        tx.State = TxState.WaitingReady;
        // READY 대기는 콜드 인스턴스 부팅+월드젠 여유 (기본 5분) - 출발지는 FREEZE로
        // 이미 조용화되어 있어 클라이언트는 로딩 화면에서 안전하게 대기한다
        tx.StepDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(_config.MigrationReadyWaitTimeoutSeconds);
        WriteWal(tx);

        TryProceedSwap(tx);
    }

    // 목적지 READY 확인 후 SWAP 발행 ( READY 게이트). 인플라이트(SwapSent)면
    // 재발신하지 않는다. 미READY면 Tick이 재시도
    private void TryProceedSwap(Transaction tx)
    {
        if (tx.State != TxState.WaitingReady) return;
        if (tx.SwapSent) return;

        InstanceInfo? target = _instances.Find(tx.TargetInstance ?? "");
        if (target == null || target.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
        {
            // 목적지가 죽은 상태 - 재스폰 후 대기 지속 (READY 대기 타임아웃이 상한)
            _instances.EnsureInstance(tx.ToDepth);
            tx.TargetInstance = _instances.FindByDepth(tx.ToDepth)?.Key;
            return;
        }
        if (target.Status is not (InstanceStatus.Ready or InstanceStatus.Idle))
        {
            return; // 부팅/월드젠 중 - Tick 재시도
        }

        string? addr = _instances.BackendAddrFor(target);
        if (addr == null) return;

        tx.SwapSent = true;
        _hub.Send(_hub.GatewayConnection, "SWAP",
            new { playerKey = tx.Player.Value, instanceId = tx.TargetInstance, backendAddr = addr, epoch = tx.Epoch },
            (ok, reason) =>
            {
                if (!ok) { Abort(tx, $"SWAP 실패: {reason}"); return; }
                // SWAP ack = 게이트웨이가 명령 보유 - WAL 상태를 Swapping으로 전이
                // (이 시점 이후 오케스트레이터가 죽어도 게이트웨이가 스왑을 수행하므로
                // 복구는 "전진"(RESUME 재발신)으로 수렴한다.)
                tx.State = TxState.Swapping;
                tx.StepDeadline = DateTime.UtcNow + StepTimeout();
                WriteWal(tx);
            });
    }

    // 게이트웨이 BACKEND_CONNECTED - 스왑 완료 시점 (목적지가 월드 준비 후 접속 성공)
    public void OnBackendConnected(PlayerKey player, string instanceId, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (tx.State != TxState.Swapping) return;
        if (!IsCurrent(tx, epoch)) return;
        if (tx.TargetInstance != null && instanceId != tx.TargetInstance)
        {
            // 구 인스턴스로 복귀 = 스왑 실패 롤백 (게이트웨이 )
            Abort(tx, $"백엔드가 목적지가 아닌 {instanceId}에 연결됨");
            return;
        }

        tx.State = TxState.Resuming;
        tx.ResumeSent = true;
        tx.ResumeRetries = 0;
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();

        SendResume(tx);
    }

    // RESUME 전송 (데이터는 보류분/디스크, ghostClientIds 재산출). ack 수령 시 WAL 전이
    private void SendResume(Transaction tx)
    {
        ControlHub.ClientConnection? targetConn = _hub.ModConnection(tx.TargetInstance ?? "");
        if (targetConn == null)
        {
            // 목적지 모드 미연결 (부팅/크래시) - Tick이 재시도 (Resuming 타임아웃이 상한)
            return;
        }

        JsonElement? payload = _dataStore.GetForMigration(tx.Player);
        // ghostClientIds: 출발 인스턴스에 남아있는 다른 온라인 플레이어들의 clientId
        // 도착 플레이어의 클라이언트가 잔존 NetPlayer를 즉시 정리하도록 10170 신호에 사용
        // 수정: 같은 목적지로 동시 마이그레이션 중인 플레이어는 제외한다
        // 포함하면 도착자 클라이언트가 함께 도착하는 유저의 NetPlayer를 파괴해
        // "서로가 안 보이는" 가시성 버그가 발생한다 (Session.InstanceId는 COMMIT 전까지
        // 출발지를 가리키므로 Migrating 상태만으로는 구분 불가)
        ushort[] ghostClientIds = _sessions.All
            .Where(s => s.InstanceId == tx.FromInstance
                && s.Session != PlayerSessionState.Offline
                && s.Key != tx.Player
                && !MigratingToSameTarget(tx, s.Key))
            .Select(s => s.ClientId)
            .ToArray();

        if (tx.IsRespawn)
        {
            // 리스폰: 데이터 없음 - 목적지가 프레시 생성 (payload 부재 + respawn 플래그)
            _hub.Send(targetConn, "RESUME",
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
            _hub.Send(targetConn, "RESUME",
                new { playerKey = tx.Player.Value, epoch = tx.Epoch, payload = payload, ghostClientIds = ghostClientIds },
                (ok, reason) =>
                {
                    if (!ok) { Abort(tx, $"RESUME 전송 실패: {reason}"); return; }
                    if (tx.State != TxState.Resuming) return;
                    WriteWal(tx); // WAL "Resuming" = 목적지가 데이터 보유
                });
        }
        // 고스트 추적 로깅 - 순차 마이그레이션에서 잔류자 없는데 고스트가 포함된 이상 케이스의 원인 특정용
    }

    public void OnResumeDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx) || tx.State != TxState.Resuming) return;
        if (!IsCurrent(tx, epoch)) return;
        tx.State = TxState.Worldgen;
        tx.StepDeadline = DateTime.UtcNow + StepTimeout();
        WriteWal(tx);

        // 조기 로딩 트리거(10016)는 구 인스턴스가 LAYER_END 시점에 이미 수행했다
        // 여기서 재전송하면 클라이언트가 재생성을 취소/재시작해 파라미터를 재수신하지
        // 못하고 영구 대기(stuck)할 수 있으므로 전송하지 않는다. WORLDGEN_DONE만 대기
    }

    public void OnWorldgenDone(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (!IsCurrent(tx, epoch)) return;

        // COMMIT
        _sessions.CommitMigration(player, tx.ToDepth, tx.TargetInstance);
        _dataStore.CommitPending(player);
        if (tx.IsRespawn)
        {
            // 리스폰 안전망: 캡처 스킵/세이브 폐기 상태의 최종 확인 (이상 시 재폐기)
            _dataStore.DeleteSave(player);
        }
        _transactions.Remove(player);
        RemoveWal(player);

        InstanceInfo? fromInstance = _instances.Find(tx.FromInstance);
        if (fromInstance != null)
        {
            _hub.Send(fromInstance.ModConnection, "RELEASE",
                new { playerKey = player.Value, epoch = tx.Epoch });
        }
        // RESUME 시 목적지 인스턴스도 동결됨 - RELEASE로 해제 (이후 퇴장 저장 정상화)
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, "RELEASE",
                new { playerKey = player.Value, epoch = tx.Epoch });
        }

        VerboseState.Line($"{player}: COMMIT — 레이어 {tx.ToDepth} 완료 (epoch {tx.Epoch}).");
        MigrationCommitted?.Invoke(player, tx.FromDepth, tx.ToDepth);
    }

    // 게이트웨이 SWAP_FAILED 보고 - 트랜잭션 중단
    public void OnSwapFailed(PlayerKey player, string reason)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        Abort(tx, $"SWAP_FAILED: {reason}");
    }

    // 마이그레이션 중 플레이어 이탈 (구 인스턴스의 NetPlayer.OnDestroy 보고)
    // 보류 데이터 확정 + 세션 Depth 증가 . Freezing(스왑 전) 상태에서만 이탈로
    // 처리하며 그 이후는 구 인스턴스 정리 부산물로 무시한다 (실퇴장은
    // SESSION_DISCONNECTED -> OnPlayerQuitDuringMigration이 항상 동시에 처리)
    public void OnPlayerLeftDuringMigration(PlayerKey player, int? epoch)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        if (tx.State != TxState.Freezing) return;
        if (!IsCurrent(tx, epoch)) return;

        EndMigrationOnLeave(tx);
    }

    // 마이그레이션 중 플레이어 퇴장 (게이트웨이 SESSION_DISCONNECTED - 실퇴장)
    public void OnPlayerQuitDuringMigration(PlayerKey player)
    {
        if (!_transactions.TryGetValue(player, out Transaction? tx)) return;
        EndMigrationOnLeave(tx);
    }

    // 이탈 확정: 데이터는 디스크가 정본(제출 시 기록됨), 위치는 복원하지 않도록
    // 세션 Depth만 toDepth로 증가시켜 영속화한다 (재접속 시 새 레이어 배정 +
    // 레이어 불일치로 위치 스킵)
    // 수정: 출발지·목적지 모드에 RELEASE를 전송해 동결을 해제한다 - 이전에는
    // 이탈 확정 시 모드가 동결 상태로 남아, 재접속한 플레이어가 조용화 타깃 게이트
    // (IsFrozenTarget)에 막혀 다른 플레이어 바디·월드 객체·복원 인벤토리가 전혀
    // 동기화되지 않는 버그가 있었다 (데이터는 유실이 아니라 동기화 차단)
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

        // 동결 해제 (퇴장이므로 UNFREEZE의 로딩 복구 경로 대신 RELEASE - epoch 가드로 멱등)
        InstanceInfo? fromInstance = _instances.Find(tx.FromInstance);
        if (fromInstance != null)
        {
            _hub.Send(fromInstance.ModConnection, "RELEASE",
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, "RELEASE",
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }

    }

    // 인스턴스 READY 통지 - WaitingReady 트랜잭션의 SWAP을 즉시 발행한다
    // (ROUTE-ON-READY 연동: Tick 폴링 대신 READY 이벤트 기반)
    public void OnInstanceReady(string instanceKey)
    {
        foreach (Transaction tx in _transactions.Values.Where(t =>
            t.State == TxState.WaitingReady && t.TargetInstance == instanceKey))
        {
            TryProceedSwap(tx);
        }
    }

    // 틱: READY 게이트 재시도 + RESUME 재발신 + 타임아웃

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Transaction tx in _transactions.Values.ToList())
        {
            // READY 게이트 재시도 (목적지 부팅 완료 대기)
            if (tx.State == TxState.WaitingReady)
            {
                if (tx.SwapSent)
                {
                    // 인플라이트 중 목적지 사망 - 재스폰 후 SWAP 재발행 준비
                    InstanceInfo? target = _instances.Find(tx.TargetInstance ?? "");
                    if (target == null || target.Status is InstanceStatus.Stopped or InstanceStatus.Crashed)
                    {
                        _instances.EnsureInstance(tx.ToDepth);
                        tx.TargetInstance = _instances.FindByDepth(tx.ToDepth)?.Key;
                        tx.SwapSent = false;
                    }
                }
                else
                {
                    TryProceedSwap(tx);
                }
            }

            // 복구 트랜잭션: WAL "Swapping" (RESUME 미발신) -> 목적지 모드 연결 시 재발신
            if (tx.State == TxState.Resuming && !tx.ResumeSent)
            {
                if (_hub.ModConnection(tx.TargetInstance ?? "") != null)
                {
                    tx.ResumeSent = true;
                    SendResume(tx);
                }
            }

            if (now < tx.StepDeadline) continue;

            if (tx.State == TxState.Resuming && tx.ResumeRetries < 2)
            {
                tx.ResumeRetries++;
                tx.StepDeadline = now + StepTimeout();
                SendResume(tx);
                continue;
            }
            Abort(tx, $"단계 타임아웃 ({tx.State})");
        }
    }

    // 롤백

    private void Abort(Transaction tx, string reason)
    {
        if (!_transactions.Remove(tx.Player)) return;
        RemoveWal(tx.Player);
        Console.WriteLine($"{tx.Player} 중단: {reason} — 롤백.");

        bool postSwap = tx.State is TxState.Resuming or TxState.Worldgen;

        PlayerState? state = _sessions.Get(tx.Player);
        if (state != null)
        {
            // 실패해도 세션을 목적지로 배치 - 재접속 시 목적지 레이어로 라우팅되고,
            // 저장 위치의 레이어(출발지) != 목적지 레이어라 위치 복원이 자동 스킵되어
            // 목적지 기본 시작 위치에 스폰된다 (레이어 끝 스폰 -> LAYER_END 재발화 -> 이중 전진 차단).
            // 목적지 인스턴스도 유휴 오판정으로 강제 정지되지 않게 세션에 고정한다.
            state.Session = PlayerSessionState.OnLayer;
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
        // pre-swap 중단: 구 인스턴스의 인벤토리는 FREEZE에서 파괴됨
        // 캡처 데이터로 즉시 재적용하도록 payload를 함께 전달
        JsonElement? payload = tx.IsRespawn ? null : _dataStore.GetForMigration(tx.Player);
                _hub.Send(fromInstance.ModConnection, "UNFREEZE",
                    new { playerKey = tx.Player.Value, epoch = tx.Epoch, payload = payload });
            }
            else
            {
                _hub.Send(fromInstance.ModConnection, "UNFREEZE",
                    new { playerKey = tx.Player.Value, epoch = tx.Epoch });
            }
        }
        // RESUME 시 목적지 인스턴스도 동결됨 - 함께 해제 (퇴장 저장 스킵 방지)
        InstanceInfo? targetInstance = _instances.Find(tx.TargetInstance ?? "");
        if (targetInstance != null && targetInstance.Key != tx.FromInstance)
        {
            _hub.Send(targetInstance.ModConnection, "UNFREEZE",
                new { playerKey = tx.Player.Value, epoch = tx.Epoch });
        }

        // 실패 추방 - 유저가 수동으로 나갔다 들어오지 않아도 복구되도록 강제 재접속
        // 모든 실패(단계 타임아웃 포함)에 적용: FREEZE로 파괴된 인벤토리는 재접속 시
        // 세이브 캡처로 복구되므로, 킥이 항상 깨끗한 재접속 복구 경로를 보장한다
        // post-swap(월드젠 타임아웃 등 - 유저가 목적지 로딩에 갇힘) -> 목적지, 그 외 -> 출발지
        string? kickInstance = postSwap ? tx.TargetInstance : tx.FromInstance;
        if (kickInstance != null)
        {
            _hub.Send(_hub.ModConnection(kickInstance), "KICK_PLAYER",
                new { playerKey = tx.Player.Value, reason = "Migration Failed. Please reconnect." });
        }
    }

    // epoch 검증 - 메시지의 epoch가 현재 트랜잭션과 일치하는지. epoch 부재 시
    // 구버전 모드/게이트웨이와의 하위 호환으로 수용한다 (멱등 규칙은 오케스트레이터가 원본)
    private static bool IsCurrent(Transaction tx, int? epoch) =>
        !epoch.HasValue || epoch.Value == tx.Epoch;

    // 다른 플레이어가 같은 목적지로 마이그레이션 진행 중인지 (고스트 제외 판정)
    private bool MigratingToSameTarget(Transaction tx, PlayerKey other) =>
        _transactions.TryGetValue(other, out Transaction? t) && t.TargetInstance == tx.TargetInstance;

    private TimeSpan StepTimeout() => TimeSpan.FromSeconds(_config.MigrationStepTimeoutSeconds);
}
