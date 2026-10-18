using System.Collections.Generic;

namespace CasuMp.Protocol;

public sealed class MapObjectQueryRequest
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public int Cell { get; set; }
    public int MaxObjects { get; set; } = 3000;
    public List<string> Categories { get; set; } = new();
    public bool FilterNames { get; set; }
    public List<string> Names { get; set; } = new();
}

public sealed class MapObjectEntry
{
    public int Key { get; set; }
    public string Category { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sprite { get; set; } = "";
    public int BlockX { get; set; }
    public int BlockY { get; set; }
}

public sealed class MapObjectCluster
{
    public int CellX { get; set; }
    public int CellY { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Count { get; set; }
    public Dictionary<string, int> Categories { get; set; } = new();
}

public sealed class MapObjectQueryResponse
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public List<MapObjectEntry> Objects { get; set; } = new();
    public List<MapObjectCluster> Clusters { get; set; } = new();
    public int Total { get; set; }
    public bool Truncated { get; set; }
    public int Cell { get; set; }
    public List<string> Names { get; set; } = new();
}

public sealed class MapObjectDetailRequest
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string ObjectKey { get; set; } = "";
}

public sealed class MapObjectDetailResponse
{
    public string RequestId { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public bool Found { get; set; }
    public string? Error { get; set; }
    public MapObjectDetail? Detail { get; set; }
}

public sealed class MapObjectDetail
{
    public int Key { get; set; }
    public string Category { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sprite { get; set; } = "";
    public int BlockX { get; set; }
    public int BlockY { get; set; }
    public float Health { get; set; }
    public float Condition { get; set; } = -1;
    public string Description { get; set; } = "";
    public MapTraderDetail? Trader { get; set; }
}

public sealed class MapTraderDetail
{
    public float Reputation { get; set; }
    public float ActualReputation { get; set; }
    public bool Hostile { get; set; }
    public int ValueGiven { get; set; }
    public int TotalValueGiven { get; set; }
    public int MaxValueGiven { get; set; }
    public List<MapTraderItem> Items { get; set; } = new();
}

public sealed class MapTraderItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public string Preference { get; set; } = "";
    public int Price { get; set; }
    public bool Bought { get; set; }
}

public sealed class MapBackgroundObject
{
    public string Sprite { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float Rotation { get; set; }
    public int SortingOrder { get; set; }
}

public sealed class MapBackgroundPayload
{
    public string InstanceKey { get; set; } = "";
    public List<MapBackgroundObject> Objects { get; set; } = new();
}
