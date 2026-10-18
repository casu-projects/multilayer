using CasuMp.Protocol;
using SkiaSharp;

namespace CasuMpServer;

public sealed class MapRenderer
{
    public const int TileScale = 8;
    private const byte BackgroundDimAlpha = 153;
    private const int BackgroundDownscale = 2;
    private const float BackgroundBlurScale = 0.6f;

    private sealed class CacheEntry
    {
        public string WorldVersion = "";
        public int Version;
        public byte[] Png = Array.Empty<byte>();
    }

    private readonly GameAssetCatalog _assets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache = new();

    public MapRenderer(GameAssetCatalog assets)
    {
        _assets = assets;
    }

    public byte[]? Render(MapRenderData data, int tileScale = TileScale,
        IReadOnlyList<MapBackgroundObject>? backgroundObjects = null, int backgroundVersion = 0)
    {
        if (!_assets.Loaded || _assets.BlockAtlas == null) return null;
        if (data.Width <= 0 || data.Height <= 0) return null;
        if (data.Grid.Length != data.Width * data.Height) return null;
        if (tileScale < 1 || tileScale > 8) return null;

        string cacheKey = $"{data.InstanceKey}:{tileScale}:{backgroundVersion}";
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached)
            && cached.WorldVersion == data.WorldVersion
            && cached.Version == data.Version
            && cached.Png.Length > 0)
        {
            return cached.Png;
        }

        int outputWidth = data.Width * tileScale;
        int outputHeight = data.Height * tileScale;
        if (outputWidth <= 0 || outputHeight <= 0) return null;
        if ((long)outputWidth * outputHeight > 80_000_000) return null;

        using var surface = SKSurface.Create(new SKImageInfo(outputWidth, outputHeight));
        if (surface == null) return null;
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(new SKColor(13, 17, 23));
        RenderBlurredBackground(canvas, data, tileScale, outputWidth, outputHeight);
        RenderObjectBackgrounds(canvas, backgroundObjects, tileScale);
        RenderBackgroundDim(canvas, outputWidth, outputHeight);
        RenderBlocks(canvas, data, tileScale);

        using SKImage image = surface.Snapshot();
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = encoded.ToArray();
        _cache[cacheKey] = new CacheEntry
        {
            WorldVersion = data.WorldVersion,
            Version = data.Version,
            Png = png,
        };
        return png;
    }

    private void RenderBlurredBackground(SKCanvas canvas, MapRenderData data,
        int tileScale, int outputWidth, int outputHeight)
    {
        int width = Math.Max(1, outputWidth / BackgroundDownscale);
        int height = Math.Max(1, outputHeight / BackgroundDownscale);
        int backgroundScale = Math.Max(1, tileScale / BackgroundDownscale);
        using SKSurface? backgroundSurface = SKSurface.Create(new SKImageInfo(width, height));
        if (backgroundSurface == null)
        {
            RenderBackground(canvas, data, tileScale);
            return;
        }

        backgroundSurface.Canvas.Clear(new SKColor(13, 17, 23));
        RenderBackground(backgroundSurface.Canvas, data, backgroundScale);
        using SKImage backgroundImage = backgroundSurface.Snapshot();
        float sigma = Math.Max(1.5f, backgroundScale * BackgroundBlurScale);
        using SKImageFilter blur = SKImageFilter.CreateBlur(sigma, sigma);
        using var paint = new SKPaint { ImageFilter = blur };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        canvas.DrawImage(backgroundImage,
            new SKRect(0, 0, outputWidth, outputHeight), sampling, paint);
    }

    private void RenderBackground(SKCanvas canvas, MapRenderData data, int tileScale)
    {
        GameAssetCatalog.BackgroundAsset? background = _assets.GetBackground(data.BiomeDepth);
        if (background == null || background.Image.Width <= 0 || background.Image.Height <= 0) return;

        int tilePixels = Math.Max(1, _assets.BlocksPerBackgroundTile * tileScale);
        using var paint = new SKPaint { IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        (byte R, byte G, byte B) tint = ParseColor(_assets.BackgroundTint);
        paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(tint.R, tint.G, tint.B), SKBlendMode.Modulate);

        SKRect source = new(0, 0, background.Image.Width, background.Image.Height);
        for (int y = 0; y < data.Height * tileScale; y += tilePixels)
        {
            for (int x = 0; x < data.Width * tileScale; x += tilePixels)
            {
                SKRect destination = new(
                    x,
                    y,
                    Math.Min(x + tilePixels, data.Width * tileScale),
                    Math.Min(y + tilePixels, data.Height * tileScale));
                canvas.DrawBitmap(background.Image, source, destination, sampling, paint);
            }
        }
    }

    private static void RenderBackgroundDim(SKCanvas canvas, int width, int height)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, BackgroundDimAlpha),
            IsAntialias = false,
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), paint);
    }

    private void RenderObjectBackgrounds(SKCanvas canvas,
        IReadOnlyList<MapBackgroundObject>? objects, int tileScale)
    {
        if (objects == null || objects.Count == 0) return;
        using var paint = new SKPaint { IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        foreach (MapBackgroundObject obj in objects)
        {
            SKBitmap? image = _assets.GetNativeObjectSprite(obj.Sprite);
            if (image == null) continue;
            float width = Math.Max(1f, obj.Width * tileScale);
            float height = Math.Max(1f, obj.Height * tileScale);
            float centerX = obj.X * tileScale;
            float centerY = obj.Y * tileScale;
            canvas.Save();
            canvas.Translate(centerX, centerY);
            canvas.RotateDegrees(-obj.Rotation);
            SKRect destination = new(-width / 2f, -height / 2f, width / 2f, height / 2f);
            canvas.DrawBitmap(image, destination, sampling, paint);
            canvas.Restore();
        }
    }

    private void RenderBlocks(SKCanvas canvas, MapRenderData data, int tileScale)
    {
        SKBitmap atlas = _assets.BlockAtlas!;
        using var paint = new SKPaint { IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        for (int y = 0; y < data.Height; y++)
        {
            int sourceRow = data.Height - 1 - y;
            for (int x = 0; x < data.Width; x++)
            {
                int block = data.Grid[sourceRow * data.Width + x];
                if (block <= 0) continue;
                if (!_assets.Blocks.TryGetValue(block, out GameAssetCatalog.BlockAsset? asset)) continue;
                if (asset.W <= 0 || asset.H <= 0) continue;

                SKRect source = new(asset.X, asset.Y, asset.X + asset.W, asset.Y + asset.H);
                SKRect destination = new(
                    x * tileScale,
                    y * tileScale,
                    (x + 1) * tileScale,
                    (y + 1) * tileScale);
                canvas.DrawBitmap(atlas, source, destination, sampling, paint);
            }
        }
    }

    private static (byte R, byte G, byte B) ParseColor(string value)
    {
        string text = value.Trim().TrimStart('#');
        if (text.Length != 6) return (128, 128, 128);
        try
        {
            return (
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text[2..4], 16),
                Convert.ToByte(text[4..6], 16));
        }
        catch
        {
            return (128, 128, 128);
        }
    }
}
