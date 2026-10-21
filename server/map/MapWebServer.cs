using CasuMp.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CasuMpServer;

internal sealed class MapWebServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly WorldSnapshotStore _store;
    private readonly ServerConfig _config;
    private readonly SteamApiClient? _steamApi;
    private readonly ControlHub? _hub;
    private readonly PlayerDetailRegistry _playerDetails;
    private readonly ObjectBackgroundStore _objectBackgrounds;
    private readonly RequestRegistry<MapObjectQueryResponse> _objectQueries;
    private readonly RequestRegistry<MapObjectDetailResponse> _objectDetails;
    private readonly InstanceManager _instances;
    private readonly GameAssetCatalog _assets;
    private readonly MapRenderer _renderer;
    private readonly ConcurrentDictionary<ulong, (string? Url, DateTime At)> _avatarCache = new();

    internal MapWebServer(WebApplication app, WorldSnapshotStore store, ServerConfig config,
        SteamApiClient? steamApi, ControlHub? hub, PlayerDetailRegistry playerDetails,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails, InstanceManager instances, GameAssetCatalog assets)
    {
        _app = app;
        _store = store;
        _config = config;
        _steamApi = steamApi;
        _hub = hub;
        _playerDetails = playerDetails;
        _objectBackgrounds = objectBackgrounds;
        _objectQueries = objectQueries;
        _objectDetails = objectDetails;
        _instances = instances;
        _assets = assets;
        _renderer = new MapRenderer(assets);
    }

    internal static MapWebServer Create(ServerConfig config, WorldSnapshotStore store, SteamApiClient? steamApi,
        ControlHub? hub, PlayerDetailRegistry playerDetails,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails, InstanceManager instances)
    {
        string contentRoot = Directory.GetCurrentDirectory();
        string webRoot = Path.Combine(contentRoot, "map", "wwwroot");
        if (!Directory.Exists(webRoot)) webRoot = Path.Combine(AppContext.BaseDirectory, "map", "wwwroot");
        if (!Directory.Exists(webRoot)) webRoot = Path.Combine(contentRoot, "wwwroot");

        GameAssetCatalog assets = GameAssetCatalog.Load(Path.Combine(webRoot, "assets", "game"));
        if (!assets.Loaded)
            Log.Info($"맵 에셋 로드 실패: {assets.Error}");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = contentRoot,
            WebRootPath = webRoot,
        });
        builder.WebHost.UseUrls(config.WebListen);
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        WebApplication app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var server = new MapWebServer(app, store, config, steamApi, hub, playerDetails,
            objectBackgrounds, objectQueries, objectDetails, instances, assets);
        server.MapEndpoints();
        return server;
    }

    public Task StartAsync() => _app.StartAsync();

    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _assets.Dispose();
    }

    private void MapEndpoints()
    {
        _app.MapGet("/api/health", () => Results.Json(new { ok = true }));

        _app.MapGet("/api/meta", () => Results.Json(new
        {
            serverName = string.IsNullOrWhiteSpace(_config.ServerName) ? "CasuMP Server" : _config.ServerName,
        }));

        _app.MapGet("/api/instances", () => Results.Json(
            _store.GetInstances().Where(instance => _instances.IsWebVisible(instance.InstanceKey)).ToList()));

        _app.MapGet("/api/instances/{instanceKey}/world", (string instanceKey, int? since) =>
        {
            WorldResponse? world = _store.GetWorld(instanceKey, since ?? -1);
            if (world == null) return Results.NotFound();
            world.TileSize = MapRenderer.TileScale;
            world.ObjectBackgroundVersion = _objectBackgrounds.GetVersion(instanceKey);
            world.MapPngUrl = $"/api/instances/{Uri.EscapeDataString(instanceKey)}/map.png?v={Uri.EscapeDataString(world.WorldVersion)}&version={world.Version}&bg={world.ObjectBackgroundVersion}";
            return Results.Json(world);
        });

        _app.MapGet("/api/instances/{instanceKey}/map.png", (string instanceKey, int? scale) =>
        {
            MapRenderData? data = _store.GetRenderData(instanceKey);
            if (data == null) return Results.NotFound();
            int tileScale = Math.Clamp(scale ?? MapRenderer.TileScale, 1, 8);
            IReadOnlyList<MapBackgroundObject> backgrounds = _objectBackgrounds.Get(instanceKey);
            int backgroundVersion = _objectBackgrounds.GetVersion(instanceKey);
            byte[]? png = _renderer.Render(data, tileScale, backgrounds, backgroundVersion);
            return png == null ? Results.NotFound() : Results.File(png, "image/png");
        });

        _app.MapGet("/api/instances/{instanceKey}/players", async (string instanceKey) =>
        {
            MapPlayersResponse? response = _store.GetPlayers(instanceKey);
            if (response == null) return Results.NotFound();

            foreach (MapPlayerView player in response.Players)
            {
                if (ulong.TryParse(player.SteamId, out ulong steamId))
                {
                    player.AvatarUrl = await GetAvatarAsync(steamId);
                }
            }
            return Results.Json(response);
        });

        _app.MapGet("/api/instances/{instanceKey}/players/{playerKey}/detail",
            async (HttpContext context, string instanceKey, string playerKey) =>
        {
            if (_hub == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!_instances.IsQueryable(instanceKey))
                return Results.Json(new { error = "instance not ready" }, statusCode: StatusCodes.Status409Conflict);
            ControlHub.ClientConnection? conn = _hub.ModConnection(instanceKey);
            if (conn == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<PlayerDetailResponse>? pending = _playerDetails.Register(requestId);
            if (pending == null)
                return Results.Json(new { error = "too many pending requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            _hub.SendNoAck(conn, MessageType.PLAYER_DETAIL_REQUEST,
                new PlayerDetailRequest { RequestId = requestId, InstanceKey = instanceKey, PlayerKey = playerKey });

            Task completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted));
            if (completed != pending.Task)
            {
                _playerDetails.Remove(requestId);
                return Results.Json(new { error = "detail timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
            }

            PlayerDetailResponse response = await pending.Task;
            if (!response.Found || response.Detail == null)
                return Results.NotFound(new { error = response.Error ?? "player not found" });
            MapRenderData? renderData = _store.GetRenderData(instanceKey);
            if (renderData != null)
                response.Detail.BlockY = renderData.Height - 1 - response.Detail.BlockY;
            return Results.Json(response.Detail);
        });

        _app.MapGet("/api/instances/{instanceKey}/objects", async (HttpContext context, string instanceKey,
            int? minX, int? minY, int? maxX, int? maxY, int? cell, string? categories, int? maxObjects,
            string? names) =>
        {
            if (_hub == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!_instances.IsQueryable(instanceKey))
                return Results.Json(new { error = "instance not ready" }, statusCode: StatusCodes.Status409Conflict);
            ControlHub.ClientConnection? connection = _hub.ModConnection(instanceKey);
            if (connection == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<MapObjectQueryResponse>? pending = _objectQueries.Register(requestId);
            if (pending == null)
                return Results.Json(new { error = "too many pending requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            _hub.SendNoAck(connection, MessageType.OBJECT_QUERY, new MapObjectQueryRequest
            {
                RequestId = requestId,
                InstanceKey = instanceKey,
                MinX = minX ?? 0,
                MinY = minY ?? 0,
                MaxX = maxX ?? int.MaxValue,
                MaxY = maxY ?? int.MaxValue,
                Cell = Math.Max(0, cell ?? 0),
                MaxObjects = Math.Max(1, maxObjects ?? 3000),
                Categories = string.IsNullOrWhiteSpace(categories)
                    ? new List<string>()
                    : categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                FilterNames = names != null,
                Names = string.IsNullOrEmpty(names)
                    ? new List<string>()
                    : names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            });

            Task completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted));
            if (completed != pending.Task)
            {
                _objectQueries.Remove(requestId);
                return Results.Json(new { error = "object query timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            return Results.Json(await pending.Task);
        });

        _app.MapGet("/api/instances/{instanceKey}/objects/{objectKey}/detail",
            async (HttpContext context, string instanceKey, string objectKey) =>
        {
            if (_hub == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!_instances.IsQueryable(instanceKey))
                return Results.Json(new { error = "instance not ready" }, statusCode: StatusCodes.Status409Conflict);
            ControlHub.ClientConnection? connection = _hub.ModConnection(instanceKey);
            if (connection == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<MapObjectDetailResponse>? pending = _objectDetails.Register(requestId);
            if (pending == null)
                return Results.Json(new { error = "too many pending requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            _hub.SendNoAck(connection, MessageType.OBJECT_DETAIL_REQUEST, new MapObjectDetailRequest
            {
                RequestId = requestId,
                InstanceKey = instanceKey,
                ObjectKey = objectKey,
            });

            Task completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted));
            if (completed != pending.Task)
            {
                _objectDetails.Remove(requestId);
                return Results.Json(new { error = "object detail timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            MapObjectDetailResponse response = await pending.Task;
            return response.Found && response.Detail != null
                ? Results.Json(response.Detail)
                : Results.NotFound(new { error = response.Error ?? "object not found" });
        });

    }

    private async Task<string?> GetAvatarAsync(ulong steamId)
    {
        if (steamId == 0 || _steamApi == null) return null;

        if (_avatarCache.TryGetValue(steamId, out (string? Url, DateTime At) cached)
            && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
        {
            return cached.Url;
        }

        string? url = await _steamApi.GetAvatarAsync(steamId);
        _avatarCache[steamId] = (url, DateTime.UtcNow);
        return url;
    }
}
