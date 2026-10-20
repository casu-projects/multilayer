using CasuMp.Protocol;
using System.Collections.Concurrent;

namespace CasuMpServer;

public sealed class WorldSnapshotStore
{
    private const int MaxDeltaHistory = 4096;

    private readonly ConcurrentDictionary<string, InstanceState> _instances = new();

    private sealed class InstanceState
    {
        public readonly object Lock = new();
        public int Depth;
        public int BiomeDepth;
        public string WorldVersion = "";
        public int Width;
        public int Height;
        public int[] Grid = Array.Empty<int>();
        public List<MapPaletteEntry> Palette = new();
        public int Version;
        public int WorldReceived;
        public bool WorldComplete;
        public readonly List<DeltaBatch> Deltas = new();
        public List<PlayerPositionEntry> Players = new();
        public bool HasSpawn;
        public int SpawnBlockX;
        public int SpawnBlockY;
        public DateTime? WorldUpdatedAt;
        public DateTime? PlayersUpdatedAt;
    }

    private sealed class DeltaBatch
    {
        public int Version;
        public List<MapBlockChange> Changes = new();
    }

    public void RegisterInstance(string instanceKey, int depth)
    {
        InstanceState state = _instances.GetOrAdd(instanceKey, _ => new InstanceState());
        lock (state.Lock)
        {
            if (depth > 0) state.Depth = depth;
        }
    }

    public int? GetBiomeDepth(string instanceKey)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock) return state.BiomeDepth;
    }

    public void RemoveInstance(string instanceKey) => _instances.TryRemove(instanceKey, out _);

    public (int Version, string WorldVersion)? BeginWorld(string instanceKey, MapWorldBegin upload)
    {
        if (upload.Width <= 0 || upload.Height <= 0) return null;
        if ((long)upload.Width * upload.Height > int.MaxValue) return null;
        if (string.IsNullOrWhiteSpace(upload.WorldVersion)) return null;

        int total = upload.Width * upload.Height;
        InstanceState state = _instances.GetOrAdd(instanceKey, _ => new InstanceState());
        lock (state.Lock)
        {
            if (upload.Depth > 0) state.Depth = upload.Depth;
            state.BiomeDepth = upload.BiomeDepth;
            state.WorldVersion = upload.WorldVersion;
            state.Width = upload.Width;
            state.Height = upload.Height;
            state.Grid = new int[total];
            state.Palette = upload.Palette.ToList();
            state.Version = 0;
            state.WorldReceived = 0;
            state.WorldComplete = total == 0;
            state.Deltas.Clear();
            state.HasSpawn = false;
            state.WorldUpdatedAt = DateTime.UtcNow;
            return (state.Version, state.WorldVersion);
        }
    }

    public (int Version, int Received, bool Complete)? ApplyWorldChunk(string instanceKey, MapWorldChunk chunk)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock)
        {
            if (state.Grid.Length == 0) return null;
            if (!string.Equals(state.WorldVersion, chunk.WorldVersion, StringComparison.Ordinal)) return null;
            if (chunk.Offset < 0 || chunk.Offset > state.WorldReceived) return null;
            if (chunk.Offset < state.WorldReceived)
                return (state.Version, state.WorldReceived, state.WorldComplete);

            long total = chunk.Runs.Sum(run => (long)Math.Max(0, run.Count));
            if (total <= 0 || chunk.Offset + total > state.Grid.Length) return null;

            var batch = new DeltaBatch { Version = state.Version + 1 };
            int offset = chunk.Offset;
            foreach (MapRleRun run in chunk.Runs)
            {
                if (run.Count <= 0) continue;
                for (int i = 0; i < run.Count; i++)
                {
                    int index = offset + i;
                    if (state.Grid[index] == run.Block) continue;
                    state.Grid[index] = run.Block;
                    batch.Changes.Add(new MapBlockChange
                    {
                        X = index % state.Width,
                        Y = index / state.Width,
                        Block = run.Block,
                    });
                }
                offset += run.Count;
            }

            state.WorldReceived = offset;
            state.WorldComplete = state.WorldReceived >= state.Grid.Length;
            state.WorldUpdatedAt = DateTime.UtcNow;
            if (batch.Changes.Count > 0)
            {
                state.Version++;
                state.Deltas.Add(batch);
                while (state.Deltas.Count > MaxDeltaHistory) state.Deltas.RemoveAt(0);
            }
            return (state.Version, state.WorldReceived, state.WorldComplete);
        }
    }

    public int? ApplyBlocks(string instanceKey, MapBlockUpload upload)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock)
        {
            if (state.Grid.Length == 0) return null;
            if (!string.Equals(state.WorldVersion, upload.WorldVersion, StringComparison.Ordinal)) return null;

            var batch = new DeltaBatch { Version = state.Version + 1 };
            foreach (MapBlockChange change in upload.Changes)
            {
                if (change.X < 0 || change.Y < 0 || change.X >= state.Width || change.Y >= state.Height)
                    continue;
                int index = change.Y * state.Width + change.X;
                if (state.Grid[index] == change.Block) continue;
                state.Grid[index] = change.Block;
                batch.Changes.Add(change);
            }

            if (batch.Changes.Count == 0) return state.Version;

            state.Version++;
            state.WorldUpdatedAt = DateTime.UtcNow;
            state.Deltas.Add(batch);
            while (state.Deltas.Count > MaxDeltaHistory) state.Deltas.RemoveAt(0);
            return state.Version;
        }
    }

    public void UpdatePlayers(PlayerPositionsPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.InstanceKey)) return;
        InstanceState state = _instances.GetOrAdd(payload.InstanceKey, _ => new InstanceState());
        lock (state.Lock)
        {
            if (string.IsNullOrWhiteSpace(state.WorldVersion) && !string.IsNullOrWhiteSpace(payload.WorldVersion))
            {
                state.WorldVersion = payload.WorldVersion;
            }
            state.Players = payload.Players.ToList();
            if (payload.HasSpawn)
            {
                state.HasSpawn = true;
                state.SpawnBlockX = payload.SpawnBlockX;
                state.SpawnBlockY = payload.SpawnBlockY;
            }
            state.PlayersUpdatedAt = DateTime.UtcNow;
        }
    }

    public List<MapInstanceInfo> GetInstances()
    {
        var result = new List<MapInstanceInfo>();
        foreach ((string key, InstanceState state) in _instances)
        {
            lock (state.Lock)
            {
                result.Add(new MapInstanceInfo
                {
                    InstanceKey = key,
                    Depth = state.Depth,
                    BiomeDepth = state.BiomeDepth,
                    WorldVersion = state.WorldVersion,
                    Width = state.Width,
                    Height = state.Height,
                    Version = state.Version,
                    PlayerCount = state.Players.Count,
                    WorldUpdatedAt = state.WorldUpdatedAt,
                    PlayersUpdatedAt = state.PlayersUpdatedAt,
                });
            }
        }
        return result
            .OrderBy(i => i.Depth)
            .ThenBy(i => i.InstanceKey, StringComparer.Ordinal)
            .ToList();
    }

    public WorldResponse? GetWorld(string instanceKey, int since)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock)
        {
            if (state.Grid.Length == 0 || string.IsNullOrWhiteSpace(state.WorldVersion)) return null;

            bool full = since < 0 || since > state.Version;
            if (!full && state.Deltas.Count > 0 && state.Deltas[0].Version > since + 1)
                full = true;

            var response = new WorldResponse
            {
                InstanceKey = instanceKey,
                WorldVersion = state.WorldVersion,
                Version = state.Version,
                BiomeDepth = state.BiomeDepth,
                Width = state.Width,
                Height = state.Height,
                Palette = state.Palette.ToList(),
                Reset = full,
            };

            if (full)
            {
                response.Runs = EncodeRle(state.Grid);
            }
            else
            {
                response.Changes = state.Deltas
                    .Where(batch => batch.Version > since)
                    .SelectMany(batch => batch.Changes)
                    .ToList();
            }
            return response;
        }
    }

    public MapRenderData? GetRenderData(string instanceKey)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock)
        {
            if (state.Grid.Length == 0 || string.IsNullOrWhiteSpace(state.WorldVersion)) return null;
            return new MapRenderData
            {
                InstanceKey = instanceKey,
                WorldVersion = state.WorldVersion,
                Version = state.Version,
                Width = state.Width,
                Height = state.Height,
                BiomeDepth = state.BiomeDepth,
                Grid = (int[])state.Grid.Clone(),
            };
        }
    }

    public MapPlayersResponse? GetPlayers(string instanceKey)
    {
        if (!_instances.TryGetValue(instanceKey, out InstanceState? state)) return null;
        lock (state.Lock)
        {
            int displayHeight = state.Height;
            var players = state.Players
                .Select(p => new MapPlayerView
                {
                    PlayerKey = p.PlayerKey,
                    SteamId = p.SteamId == 0 ? "" : p.SteamId.ToString(),
                    Name = p.Name,
                    X = p.X,
                    Y = p.Y,
                    BlockX = p.BlockX,
                    BlockY = displayHeight > 0 ? displayHeight - 1 - p.BlockY : p.BlockY,
                    Dead = p.Dead,
                    HeadSprite = p.HeadSprite,
                    EyeSprite = p.EyeSprite,
                    UpdatedAt = state.PlayersUpdatedAt,
                })
                .ToList();

            return new MapPlayersResponse
            {
                InstanceKey = instanceKey,
                WorldVersion = state.WorldVersion,
                HasSpawn = state.HasSpawn,
                SpawnBlockX = state.SpawnBlockX,
                SpawnBlockY = state.SpawnBlockY,
                UpdatedAt = state.PlayersUpdatedAt,
                Players = players,
            };
        }
    }

    private static List<MapRleRun> EncodeRle(int[] grid)
    {
        var runs = new List<MapRleRun>();
        if (grid.Length == 0) return runs;

        int current = grid[0];
        int count = 1;
        for (int i = 1; i < grid.Length; i++)
        {
            if (grid[i] == current)
            {
                count++;
                continue;
            }
            runs.Add(new MapRleRun { Count = count, Block = current });
            current = grid[i];
            count = 1;
        }
        runs.Add(new MapRleRun { Count = count, Block = current });
        return runs;
    }
}
