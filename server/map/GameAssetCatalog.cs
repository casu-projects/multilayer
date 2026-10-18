using SkiaSharp;
using System.Text.Json;

namespace CasuMpServer;

public sealed class GameAssetCatalog
{
    public sealed class BlockAsset
    {
        public int Block { get; init; }
        public string Name { get; init; } = "";
        public string Sprite { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public int W { get; init; }
        public int H { get; init; }
    }

    public sealed class BackgroundAsset
    {
        public int Depth { get; init; }
        public string Name { get; init; } = "";
        public SKBitmap Image { get; init; } = null!;
    }

    public SKBitmap? BlockAtlas { get; private set; }
    public IReadOnlyDictionary<int, BlockAsset> Blocks => _blocks;
    public IReadOnlyDictionary<int, BackgroundAsset> Backgrounds => _backgrounds;
    public string BackgroundTint { get; private set; } = "#808080";
    public int BlocksPerBackgroundTile { get; private set; } = 64;
    public int SourceTileWidth { get; private set; } = 1;
    public int SourceTileHeight { get; private set; } = 1;
    public bool Loaded { get; private set; }
    public string? Error { get; private set; }

    private sealed class NativeSpriteEntry
    {
        public string Name { get; init; } = "";
        public string File { get; init; } = "";
    }

    private readonly Dictionary<int, BlockAsset> _blocks = new();
    private readonly Dictionary<int, BackgroundAsset> _backgrounds = new();
    private readonly Dictionary<string, NativeSpriteEntry> _nativeSprites = new();
    private readonly Dictionary<string, SKBitmap?> _nativeCache = new();
    private readonly List<SKBitmap> _ownedImages = new();
    private readonly object _nativeLock = new();
    private string _assetsRoot = "";

    public static GameAssetCatalog Load(string assetsRoot)
    {
        var catalog = new GameAssetCatalog();
        try
        {
            string manifestPath = Path.Combine(assetsRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                catalog.Error = $"manifest not found: {manifestPath}";
                return catalog;
            }

            catalog._assetsRoot = assetsRoot;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = doc.RootElement;
            catalog.SourceTileWidth = root.TryGetProperty("tileSize", out JsonElement tw) ? tw.GetInt32() : 1;
            catalog.SourceTileHeight = root.TryGetProperty("tileHeight", out JsonElement th) ? th.GetInt32() : catalog.SourceTileWidth;

            if (root.TryGetProperty("blocks", out JsonElement blocks))
            {
                string atlas = blocks.GetProperty("atlas").GetString() ?? "";
                string atlasPath = Path.Combine(assetsRoot, atlas);
                if (File.Exists(atlasPath))
                {
                    catalog.BlockAtlas = SKBitmap.Decode(atlasPath);
                    if (catalog.BlockAtlas != null) catalog._ownedImages.Add(catalog.BlockAtlas);
                }
                foreach (JsonElement entry in blocks.GetProperty("entries").EnumerateArray())
                {
                    var asset = new BlockAsset
                    {
                        Block = entry.GetProperty("block").GetInt32(),
                        Name = entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                        Sprite = entry.TryGetProperty("sprite", out JsonElement sprite) ? sprite.GetString() ?? "" : "",
                        X = entry.GetProperty("x").GetInt32(),
                        Y = entry.GetProperty("y").GetInt32(),
                        W = entry.GetProperty("w").GetInt32(),
                        H = entry.GetProperty("h").GetInt32(),
                    };
                    catalog._blocks[asset.Block] = asset;
                }
            }

            if (root.TryGetProperty("backgrounds", out JsonElement backgrounds))
            {
                catalog.BackgroundTint = backgrounds.TryGetProperty("tint", out JsonElement tint)
                    ? tint.GetString() ?? "#808080"
                    : "#808080";
                catalog.BlocksPerBackgroundTile = backgrounds.TryGetProperty("blocksPerTile", out JsonElement bpt)
                    ? bpt.GetInt32()
                    : 64;
                foreach (JsonElement entry in backgrounds.GetProperty("entries").EnumerateArray())
                {
                    string relative = entry.GetProperty("file").GetString() ?? "";
                    string path = Path.Combine(assetsRoot, relative);
                    if (!File.Exists(path)) continue;
                    SKBitmap? image = SKBitmap.Decode(path);
                    if (image == null) continue;
                    catalog._ownedImages.Add(image);
                    var asset = new BackgroundAsset
                    {
                        Depth = entry.GetProperty("depth").GetInt32(),
                        Name = entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                        Image = image,
                    };
                    catalog._backgrounds[asset.Depth] = asset;
                }
            }

            if (root.TryGetProperty("objects", out JsonElement objects)
                && objects.TryGetProperty("native", out JsonElement native))
            {
                string nativePath = Path.Combine(assetsRoot, native.GetString() ?? "");
                if (File.Exists(nativePath))
                {
                    using JsonDocument nativeDoc = JsonDocument.Parse(File.ReadAllText(nativePath));
                    foreach (JsonElement entry in nativeDoc.RootElement.EnumerateArray())
                    {
                        string name = entry.TryGetProperty("name", out JsonElement nameElement)
                            ? nameElement.GetString() ?? ""
                            : "";
                        string file = entry.TryGetProperty("file", out JsonElement fileElement)
                            ? fileElement.GetString() ?? ""
                            : "";
                        if (name.Length == 0 || file.Length == 0) continue;
                        catalog._nativeSprites[name] = new NativeSpriteEntry { Name = name, File = file };
                    }
                }
            }

            catalog.Loaded = catalog.BlockAtlas != null && catalog._blocks.Count > 0;
            if (!catalog.Loaded) catalog.Error = "block atlas missing";
            return catalog;
        }
        catch (Exception ex)
        {
            catalog.Error = ex.Message;
            return catalog;
        }
    }

    public BackgroundAsset? GetBackground(int biomeDepth)
    {
        if (_backgrounds.TryGetValue(biomeDepth, out BackgroundAsset? exact)) return exact;
        if (_backgrounds.TryGetValue(0, out BackgroundAsset? fallback)) return fallback;
        return null;
    }

    public SKBitmap? GetNativeObjectSprite(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_nativeLock)
        {
            if (_nativeCache.TryGetValue(name, out SKBitmap? cached)) return cached;
            if (!_nativeSprites.TryGetValue(name, out NativeSpriteEntry? entry))
            {
                _nativeCache[name] = null;
                return null;
            }
            string path = Path.Combine(_assetsRoot, entry.File);
            SKBitmap? image = File.Exists(path) ? SKBitmap.Decode(path) : null;
            _nativeCache[name] = image;
            if (image != null) _ownedImages.Add(image);
            return image;
        }
    }

    public void Dispose()
    {
        foreach (SKBitmap image in _ownedImages)
        {
            image.Dispose();
        }
        _ownedImages.Clear();
        _nativeCache.Clear();
    }
}
