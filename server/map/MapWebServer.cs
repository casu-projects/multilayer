using CasuMp.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ChatHistoryStore _chatHistory;
    private readonly WebChatService _webChat;
    private readonly ObjectBackgroundStore _objectBackgrounds;
    private readonly RequestRegistry<MapObjectQueryResponse> _objectQueries;
    private readonly RequestRegistry<MapObjectDetailResponse> _objectDetails;
    private readonly GameAssetCatalog _assets;
    private readonly MapRenderer _renderer;
    private readonly ConcurrentDictionary<ulong, (string? Url, DateTime At)> _avatarCache = new();

    internal MapWebServer(WebApplication app, WorldSnapshotStore store, ServerConfig config,
        SteamApiClient? steamApi, ControlHub? hub, PlayerDetailRegistry playerDetails,
        ChatHistoryStore chatHistory, WebChatService webChat,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails, GameAssetCatalog assets)
    {
        _app = app;
        _store = store;
        _config = config;
        _steamApi = steamApi;
        _hub = hub;
        _playerDetails = playerDetails;
        _chatHistory = chatHistory;
        _webChat = webChat;
        _objectBackgrounds = objectBackgrounds;
        _objectQueries = objectQueries;
        _objectDetails = objectDetails;
        _assets = assets;
        _renderer = new MapRenderer(assets);
    }

    internal static MapWebServer Create(ServerConfig config, WorldSnapshotStore store, SteamApiClient? steamApi,
        ControlHub? hub, PlayerDetailRegistry playerDetails, ChatHistoryStore chatHistory, WebChatService webChat,
        ObjectBackgroundStore objectBackgrounds, RequestRegistry<MapObjectQueryResponse> objectQueries,
        RequestRegistry<MapObjectDetailResponse> objectDetails)
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

        var server = new MapWebServer(app, store, config, steamApi, hub, playerDetails, chatHistory, webChat,
            objectBackgrounds, objectQueries, objectDetails, assets);
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

        _app.MapGet("/api/chat", (long? since) => Results.Json(_chatHistory.GetSince(since ?? 0)));

        _app.MapGet("/api/chat/commands", () => Results.Json(_webChat.GetCommands()));

        _app.MapPost("/api/chat/command", async (HttpContext context, ChatCommandRequest request) =>
        {
            Task<ChatHistoryMessage?> task = _webChat.ExecuteCommandAsync(request.Command);
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted));
            if (completed != task)
                return Results.Json(new { error = "command timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
            ChatHistoryMessage? message = await task;
            return message == null
                ? Results.BadRequest(new { error = "empty command" })
                : Results.Json(message);
        });

        _app.MapPost("/api/chat", (ChatSendRequest request) =>
        {
            ChatHistoryMessage? message = _webChat.Send(request.Message);
            return message == null
                ? Results.BadRequest(new { error = "empty message" })
                : Results.Json(message);
        });

        _app.MapGet("/api/instances", () => Results.Json(_store.GetInstances()));

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
            ControlHub.ClientConnection? conn = _hub.ModConnection(instanceKey);
            if (conn == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<PlayerDetailResponse> pending = _playerDetails.Register(requestId);
            _hub.SendNoAck(conn, MessageTypes.PlayerDetailRequest,
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
            ControlHub.ClientConnection? connection = _hub.ModConnection(instanceKey);
            if (connection == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<MapObjectQueryResponse> pending = _objectQueries.Register(requestId);
            _hub.SendNoAck(connection, MessageTypes.ObjectQuery, new MapObjectQueryRequest
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
            ControlHub.ClientConnection? connection = _hub.ModConnection(instanceKey);
            if (connection == null) return Results.NotFound();

            string requestId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<MapObjectDetailResponse> pending = _objectDetails.Register(requestId);
            _hub.SendNoAck(connection, MessageTypes.ObjectDetailRequest, new MapObjectDetailRequest
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

        _app.MapPost("/api/instances/{instanceKey}/world", (HttpRequest request, string instanceKey, MapWorldUpload upload) =>
        {
            if (!CheckToken(request)) return Results.Unauthorized();
            var result = _store.SetWorld(instanceKey, upload);
            return result == null
                ? Results.BadRequest(new { error = "invalid world snapshot" })
                : Results.Json(new { ok = true, version = result.Value.Version, worldVersion = result.Value.WorldVersion });
        });

        _app.MapPost("/api/instances/{instanceKey}/blocks", (HttpRequest request, string instanceKey, MapBlockUpload upload) =>
        {
            if (!CheckToken(request)) return Results.Unauthorized();
            int? version = _store.ApplyBlocks(instanceKey, upload);
            return version == null
                ? Results.Json(new { error = "unknown instance or world version mismatch" }, statusCode: 409)
                : Results.Json(new { ok = true, version = version.Value });
        });
    }

    private bool CheckToken(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_config.WebUploadToken)) return false;

        string? token = request.Headers["X-Casu-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            string authorization = request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                token = authorization[prefix.Length..];
        }
        if (string.IsNullOrEmpty(token)) return false;

        byte[] expected = Encoding.UTF8.GetBytes(_config.WebUploadToken);
        byte[] actual = Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
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
