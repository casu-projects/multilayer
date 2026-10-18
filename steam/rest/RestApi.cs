using CasuMp.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CasuMpSteam;

internal static class RestApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Json(new { ok = true }));
        app.MapGet("/api/status", (SteamService service) => Results.Json(service.GetStatus()));
        app.MapGet("/api/steam/avatar/{steamId}", async (SteamService service, ulong steamId, CancellationToken ct) =>
        {
            string? avatar = await service.GetAvatarAsync(steamId, ct);
            return avatar == null
                ? Results.NotFound()
                : Results.Json(new { avatarFull = avatar });
        });

        app.MapGet("/api/lobbies", async (SteamService service, string? filter, CancellationToken ct) =>
        {
            try
            {
                LobbyListResponse result = await service.GetLobbiesAsync(filter, ct);
                return Results.Json(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 503);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/lobby/metadata", (SteamService service, LobbyMetadataPayload payload) =>
        {
            service.PostMetadata(payload);
            return Results.Accepted();
        });

        app.MapPost("/api/reload", (SteamService service) =>
        {
            service.Reload();
            return Results.Accepted();
        });

        app.MapPost("/api/login", (SteamService service, LoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { error = "username/password required" });
            }
            LoginSession? session = service.StartLogin(request.Username, request.Password, request.PreferCode);
            if (session == null)
            {
                return Results.Conflict(new { error = "login already in progress" });
            }
            return Results.Json(new { loginId = session.Id }, statusCode: 202);
        });

        app.MapGet("/api/login/{id}", (SteamService service, string id) =>
        {
            LoginSession? session = service.GetLogin(id);
            return session == null
                ? Results.NotFound(new { error = "login not found" })
                : Results.Json(session.Snapshot());
        });

        app.MapPost("/api/login/{id}/code", (SteamService service, string id, LoginCodeRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return Results.BadRequest(new { error = "code required" });
            }
            return service.SubmitLoginCode(id, request.Code)
                ? Results.Accepted()
                : Results.Conflict(new { error = "code not expected" });
        });

        app.MapPost("/api/login/{id}/cancel", (SteamService service, string id) =>
            service.CancelLogin(id) ? Results.Accepted() : Results.NotFound());
    }
}
