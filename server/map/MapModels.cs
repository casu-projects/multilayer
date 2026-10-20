namespace CasuMpServer;

public sealed class MapInstanceInfo
{
    public string InstanceKey { get; set; } = "";
    public int Depth { get; set; }
    public int BiomeDepth { get; set; }
    public string WorldVersion { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Version { get; set; }
    public int PlayerCount { get; set; }
    public DateTime? WorldUpdatedAt { get; set; }
    public DateTime? PlayersUpdatedAt { get; set; }
}

public sealed class WorldResponse
{
    public string InstanceKey { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public int Version { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BiomeDepth { get; set; }
    public int ObjectBackgroundVersion { get; set; }
    public string MapPngUrl { get; set; } = "";
    public int TileSize { get; set; } = 8;
    public List<MapPaletteEntry> Palette { get; set; } = new();
    public List<MapRleRun>? Runs { get; set; }
    public List<MapBlockChange>? Changes { get; set; }
    public bool Reset { get; set; }
}

public sealed class MapPlayerView
{
    public string PlayerKey { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public int BlockX { get; set; }
    public int BlockY { get; set; }
    public bool Dead { get; set; }
    public string HeadSprite { get; set; } = "";
    public string EyeSprite { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class MapPlayersResponse
{
    public string InstanceKey { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public bool HasSpawn { get; set; }
    public int SpawnBlockX { get; set; }
    public int SpawnBlockY { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<MapPlayerView> Players { get; set; } = new();
}

public sealed class MapRenderData
{
    public string InstanceKey { get; set; } = "";
    public string WorldVersion { get; set; } = "";
    public int Version { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BiomeDepth { get; set; }
    public int[] Grid { get; set; } = Array.Empty<int>();
}
