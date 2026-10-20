using CasuMp.Protocol;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Together;
using UnityEngine;

namespace CasuMod;

public sealed class MapModule : MonoBehaviour
{
    private const int WorldChunkRunCount = 20_000;
    private const int WorldAckTimeoutSeconds = 5;
    private const int WorldBeginMaxAttempts = 6;
    private const int WorldChunkMaxAttempts = 5;
    private const int WorldUploadMaxRestarts = 2;
    private const int WorldUploadTimeoutSeconds = 600;
    private const int BlocksAckTimeoutSeconds = 10;

    public static MapModule Instance { get; private set; }

    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), int> _pendingBlocks = new();

    private TaskCompletionSource<MapUploadAck>? _worldAck;
    private TaskCompletionSource<MapUploadAck>? _blocksAck;
    private string _worldAckId = "";
    private string _blocksAckId = "";
    private string _instanceKey = "";
    private int _depth = 1;
    private volatile string _worldVersion = "";
    private volatile CancellationTokenSource? _worldUploadCts;
    private int _serverVersion;
    private volatile bool _initialSent;
    private bool _sendingBlocks;
    private float _nextPlayersAt;
    private float _nextBlocksAt;
    private string _lastError = "";

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        _worldVersion = "";
        CancelWorldUpload();
    }

    private void Update()
    {
        if (OrchestratorClient.Instance != null)
        {
            _instanceKey = OrchestratorClient.Instance.InstanceKey;
            _depth = OrchestratorClient.Instance.InstanceDepth;
        }

        if (Time.unscaledTime >= _nextPlayersAt)
        {
            _nextPlayersAt = Time.unscaledTime + 1f;
            SendPlayerPositions();
        }

        if (_initialSent && Time.unscaledTime >= _nextBlocksAt)
        {
            _nextBlocksAt = Time.unscaledTime + 1f;
            _ = SendPendingBlocksAsync();
        }
    }

    public void OnWorldGenerated()
    {
        ObjectIndexModule.Instance?.RefreshNow();

        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.width == 0 || world.height == 0) return;

        OrchestratorClient? client = OrchestratorClient.Instance;
        string instanceKey = client?.InstanceKey ?? "";
        if (string.IsNullOrEmpty(instanceKey)) return;
        _instanceKey = instanceKey;
        _depth = client?.InstanceDepth ?? _depth;

        _worldVersion = Guid.NewGuid().ToString("N");
        CancelWorldUpload();
        _serverVersion = 0;
        _initialSent = false;
        lock (_lock) _pendingBlocks.Clear();

        try
        {
            (MapWorldBegin begin, List<MapRleRun> runs) = BuildWorldSnapshot(world, instanceKey);
            var cts = new CancellationTokenSource();
            _worldUploadCts = cts;
            _ = UploadWorldAsync(begin, runs, cts);
        }
        catch (Exception ex)
        {
            LogError($"월드 스냅샷 생성 실패: {ex.Message}");
        }
    }

    private void CancelWorldUpload()
    {
        try
        {
            _worldUploadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private (MapWorldBegin Begin, List<MapRleRun> Runs) BuildWorldSnapshot(WorldGeneration world, string instanceKey)
    {
        int width = (int)world.width;
        int height = (int)world.height;

        var palette = new List<MapPaletteEntry>();
        Color[] colors = world.tileColors ?? Array.Empty<Color>();
        for (int i = 0; i < colors.Length; i++)
        {
            BlockInfo info = world.GetBlockInfo((ushort)i);
            palette.Add(new MapPaletteEntry
            {
                Block = i,
                Color = "#" + ColorUtility.ToHtmlStringRGB(colors[i]),
                Name = info?.name ?? $"Block {i}",
            });
        }

        var runs = new List<MapRleRun>();
        int current = -1;
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int block = world.GetBlock(new Vector2Int(x, y));
                if (block == current)
                {
                    count++;
                    continue;
                }
                if (count > 0) runs.Add(new MapRleRun { Count = count, Block = current });
                current = block;
                count = 1;
            }
        }
        if (count > 0) runs.Add(new MapRleRun { Count = count, Block = current });

        var begin = new MapWorldBegin
        {
            InstanceKey = instanceKey,
            Depth = _depth,
            BiomeDepth = world.biomeDepth,
            WorldVersion = _worldVersion,
            Width = width,
            Height = height,
            Palette = palette,
        };
        return (begin, runs);
    }

    private async Task UploadWorldAsync(MapWorldBegin begin, List<MapRleRun> runs, CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        string worldVersion = begin.WorldVersion;
        try
        {
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts.CancelAfter(TimeSpan.FromSeconds(WorldUploadTimeoutSeconds));
            CancellationToken uploadToken = uploadCts.Token;

            for (int restart = 0; restart <= WorldUploadMaxRestarts; restart++)
            {
                if (!IsCurrentWorld(worldVersion, uploadToken)) return;

                begin.RequestId = Guid.NewGuid().ToString("N");
                MapUploadAck? beginAck = await SendWorldWithRetryAsync(
                    MessageType.MAP_WORLD_BEGIN, begin, begin.RequestId, worldVersion,
                    WorldBeginMaxAttempts, uploadToken);
                if (beginAck == null)
                {
                    LogError("월드 업로드 시작 실패: ACK 없음");
                    return;
                }
                if (!beginAck.Ok)
                {
                    LogError($"월드 업로드 시작 거부: {beginAck.Error}");
                    return;
                }

                int total = begin.Width * begin.Height;
                int offset = 0;
                bool restartUpload = false;
                int chunkCount = (runs.Count + WorldChunkRunCount - 1) / WorldChunkRunCount;

                for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
                {
                    if (!IsCurrentWorld(worldVersion, uploadToken)) return;

                    int start = chunkIndex * WorldChunkRunCount;
                    int count = Math.Min(WorldChunkRunCount, runs.Count - start);
                    List<MapRleRun> chunkRuns = runs.GetRange(start, count);
                    int chunkPixels = 0;
                    foreach (MapRleRun run in chunkRuns) chunkPixels += run.Count;

                    var chunk = new MapWorldChunk
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        WorldVersion = worldVersion,
                        Offset = offset,
                        Runs = chunkRuns,
                    };
                    MapUploadAck? chunkAck = await SendWorldWithRetryAsync(
                        MessageType.MAP_WORLD_CHUNK, chunk, chunk.RequestId, worldVersion,
                        WorldChunkMaxAttempts, uploadToken);
                    if (chunkAck == null)
                    {
                        LogError($"월드 청크 업로드 실패: ACK 없음 ({offset}/{total})");
                        return;
                    }
                    if (!chunkAck.Ok)
                    {
                        LogError($"월드 청크 거부, 업로드 재시작: {chunkAck.Error}");
                        restartUpload = true;
                        break;
                    }
                    if (!IsCurrentWorld(worldVersion, uploadToken)) return;

                    int nextOffset = offset + chunkPixels;
                    if (chunkAck.Received > nextOffset) nextOffset = chunkAck.Received;
                    offset = nextOffset;
                    _serverVersion = chunkAck.Version;
                    if (chunkAck.Complete || offset >= total) break;
                }

                if (restartUpload) continue;
                if (offset < total)
                {
                    LogError($"월드 업로드 미완료: {offset}/{total}");
                    return;
                }

                if (!IsCurrentWorld(worldVersion, uploadToken)) return;
                _initialSent = true;
                Plugin.Log.LogInfo($"[Map] 월드 스냅샷 전송 완료 ({begin.Width}x{begin.Height}, v{_serverVersion}).");
                return;
            }

            LogError("월드 업로드 재시작 한도 초과");
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentWorld(worldVersion, CancellationToken.None))
                LogError("월드 업로드 시간 초과");
        }
        catch (Exception ex)
        {
            if (IsCurrentWorld(worldVersion, CancellationToken.None))
                LogError($"월드 업로드 오류: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_worldUploadCts, cts)) _worldUploadCts = null;
            cts.Dispose();
        }
    }

    private async Task<MapUploadAck?> SendWorldWithRetryAsync(MessageType type, object payload,
        string requestId, string worldVersion, int maxAttempts, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!await WaitForConnectionAsync(worldVersion, ct)) return null;

            MapUploadAck? ack = await SendWorldOnceAsync(type, payload, requestId, ct);
            if (ack != null) return ack;
            if (!IsCurrentWorld(worldVersion, ct)) return null;
            if (attempt >= maxAttempts) break;

            Plugin.Log.LogWarning($"[Map] {type} ACK 없음, 재시도 {attempt}/{maxAttempts - 1} ({worldVersion})");
            if (!await DelayBeforeRetryAsync(attempt, ct)) return null;
        }
        return null;
    }

    private async Task<MapUploadAck?> SendWorldOnceAsync(MessageType type, object payload,
        string requestId, CancellationToken ct)
    {
        OrchestratorClient? client = OrchestratorClient.Instance;
        if (client == null || !client.Connected) return null;

        var source = new TaskCompletionSource<MapUploadAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        _worldAck = source;
        _worldAckId = requestId;
        try
        {
            client.SendEvent(type, payload);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(WorldAckTimeoutSeconds), ct);
            Task completed = await Task.WhenAny(source.Task, timeout);
            if (completed == source.Task) return await source.Task;
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            if (ReferenceEquals(_worldAck, source))
            {
                _worldAck = null;
                _worldAckId = "";
            }
        }
    }

    private async Task<bool> WaitForConnectionAsync(string worldVersion, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsCurrentWorld(worldVersion, ct)) return false;
            OrchestratorClient? client = OrchestratorClient.Instance;
            if (client != null && client.Connected) return true;
            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<bool> DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        int delayMs = 500 * (1 << Math.Min(attempt - 1, 3));
        try
        {
            await Task.Delay(delayMs, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsCurrentWorld(string worldVersion, CancellationToken ct)
    {
        return !ct.IsCancellationRequested
            && string.Equals(_worldVersion, worldVersion, StringComparison.Ordinal);
    }

    internal void HandleWorldAck(MapUploadAck ack)
    {
        if (ack == null || ack.RequestId != _worldAckId) return;
        _worldAck?.TrySetResult(ack);
    }

    internal void HandleBlocksAck(MapUploadAck ack)
    {
        if (ack == null || ack.RequestId != _blocksAckId) return;
        _blocksAck?.TrySetResult(ack);
    }

    public void OnBlockChanged(Vector2Int pos, ushort block)
    {
        if (string.IsNullOrEmpty(_worldVersion)) return;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld) return;

        lock (_lock)
        {
            _pendingBlocks[(pos.x, pos.y)] = block;
        }
    }

    private async Task SendPendingBlocksAsync()
    {
        if (_sendingBlocks) return;

        List<MapBlockChange> changes;
        lock (_lock)
        {
            if (_pendingBlocks.Count == 0) return;
            changes = new List<MapBlockChange>(_pendingBlocks.Count);
            foreach (KeyValuePair<(int X, int Y), int> pair in _pendingBlocks)
            {
                changes.Add(new MapBlockChange { X = pair.Key.X, Y = pair.Key.Y, Block = pair.Value });
            }
            _pendingBlocks.Clear();
        }

        _sendingBlocks = true;
        string worldVersion = _worldVersion;
        try
        {
            OrchestratorClient? client = OrchestratorClient.Instance;
            if (client == null || !client.Connected)
            {
                MergeBack(changes);
                return;
            }

            var upload = new MapBlockUpload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                WorldVersion = worldVersion,
                BaseVersion = _serverVersion,
                Changes = changes,
            };
            var source = new TaskCompletionSource<MapUploadAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            _blocksAck = source;
            _blocksAckId = upload.RequestId;
            client.SendEvent(MessageType.MAP_BLOCKS_UPLOAD, upload);
            Task completed = await Task.WhenAny(source.Task,
                Task.Delay(TimeSpan.FromSeconds(BlocksAckTimeoutSeconds)));
            MapUploadAck? ack = completed == source.Task ? await source.Task : null;
            if (ack == null || !ack.Ok)
            {
                if (worldVersion == _worldVersion) MergeBack(changes);
                LogError($"블록 델타 실패: {ack?.Error ?? "timeout"}");
                return;
            }
            if (worldVersion == _worldVersion) _serverVersion = ack.Version;
        }
        catch (Exception ex)
        {
            if (worldVersion == _worldVersion) MergeBack(changes);
            LogError($"블록 델타 오류: {ex.Message}");
        }
        finally
        {
            _blocksAck = null;
            _blocksAckId = "";
            _sendingBlocks = false;
        }
    }

    private void MergeBack(List<MapBlockChange> changes)
    {
        lock (_lock)
        {
            foreach (MapBlockChange change in changes)
            {
                (int, int) key = (change.X, change.Y);
                if (!_pendingBlocks.ContainsKey(key)) _pendingBlocks[key] = change.Block;
            }
        }
    }

    private void SendPlayerPositions()
    {
        OrchestratorClient client = OrchestratorClient.Instance;
        if (client == null || !client.Connected) return;

        WorldGeneration world = WorldGeneration.world;
        if (world == null) return;

        var entries = new List<PlayerPositionEntry>();
        foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers) AddPlayer(entries, player, world, false);
        foreach (ScavPlayer player in ScavPlayer.AllDeadPlayers) AddPlayer(entries, player, world, true);

        var payload = new PlayerPositionsPayload
        {
            InstanceKey = _instanceKey,
            WorldVersion = _worldVersion,
            Players = entries,
        };
        if (Body_PlaceBody_MultiplayerPatch.has_spawn_location)
        {
            Vector2Int spawn = world.WorldToBlockPos(Body_PlaceBody_MultiplayerPatch.SpawnLocation);
            payload.HasSpawn = true;
            payload.SpawnBlockX = spawn.x;
            payload.SpawnBlockY = (int)world.height - 1 - spawn.y;
        }
        client.SendEvent(MessageType.PLAYER_POSITIONS, payload);
    }

    private static void AddPlayer(List<PlayerPositionEntry> entries, ScavPlayer player,
        WorldGeneration world, bool dead)
    {
        if (player == null) return;

        Vector2 pos = player.pos;
        Vector2Int block = world.WorldToBlockPos(pos);
        FacialExpression face = player.body != null ? player.body.GetComponentInChildren<FacialExpression>() : null;
        entries.Add(new PlayerPositionEntry
        {
            PlayerKey = MigrationModule.PlayerKeyOf(player),
            SteamId = player.SteamId,
            Name = player.playerName ?? "",
            X = pos.x,
            Y = pos.y,
            BlockX = block.x,
            BlockY = block.y,
            Dead = dead,
            HeadSprite = face?.head != null && face.head.sprite != null ? face.head.sprite.name : "",
            EyeSprite = face?.eyes != null && face.eyes.sprite != null ? face.eyes.sprite.name : "",
        });
    }

    private void LogError(string message)
    {
        if (_lastError == message) return;
        _lastError = message;
        Plugin.Log.LogWarning($"[Map] {message}");
    }
}
