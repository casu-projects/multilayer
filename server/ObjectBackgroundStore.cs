using CasuMp.Protocol;
using System.Collections.Concurrent;

namespace CasuMpServer;

public sealed class ObjectBackgroundStore
{
    private sealed class Entry
    {
        public readonly object Lock = new();
        public List<MapBackgroundObject> Objects = new();
        public string Signature = "";
        public int Version;
    }

    private readonly ConcurrentDictionary<string, Entry> _instances = new();

    public void Set(string instanceKey, List<MapBackgroundObject> objects)
    {
        string signature = string.Join("|", objects
            .OrderBy(o => o.SortingOrder)
            .ThenBy(o => o.Sprite, StringComparer.Ordinal)
            .ThenBy(o => o.X)
            .ThenBy(o => o.Y)
            .Select(o => $"{o.Sprite}:{o.X}:{o.Y}:{o.Width}:{o.Height}:{o.Rotation}:{o.SortingOrder}"));
        Entry entry = _instances.GetOrAdd(instanceKey, _ => new Entry());
        lock (entry.Lock)
        {
            if (entry.Signature == signature) return;
            entry.Signature = signature;
            entry.Objects = objects.ToList();
            entry.Version++;
        }
    }

    public IReadOnlyList<MapBackgroundObject> Get(string instanceKey)
    {
        if (!_instances.TryGetValue(instanceKey, out Entry? entry)) return Array.Empty<MapBackgroundObject>();
        lock (entry.Lock) return entry.Objects.ToList();
    }

    public int GetVersion(string instanceKey)
    {
        if (!_instances.TryGetValue(instanceKey, out Entry? entry)) return 0;
        lock (entry.Lock) return entry.Version;
    }

    public void Remove(string instanceKey) => _instances.TryRemove(instanceKey, out _);
}
