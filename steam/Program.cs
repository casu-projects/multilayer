using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CasuMpSteam;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
        {
            PrintUsage();
            return 0;
        }

        string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        string configPath = GetOption(args, "--config") ?? "steam.json";

        switch (command)
        {
            case "serve":
                return await ServeAsync(configPath);

            default:
                PrintUsage();
                return 1;
        }
    }

    private static async Task<int> ServeAsync(string configPath)
    {
        SteamConfig config = SteamConfig.Load(configPath);

        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.WebHost.UseUrls(config.RestListen);
            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.PropertyNameCaseInsensitive = true;
            });
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton<SteamService>();
            builder.Services.AddHostedService<SteamWorker>();

            WebApplication app = builder.Build();

            RestApi.Map(app, config);

            SteamLog.Info($"REST API 시작: {config.RestListen}");
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            SteamLog.Info($"서버 시작 실패: {ex.Message}");
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: casu-steam [serve] [--config <path>]");
        Console.WriteLine();
        Console.WriteLine("  serve                 Steam 로비/P2P 브리지 + REST API 실행 (기본값)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --config <path>       설정 파일 (기본 steam.json)");
        Console.WriteLine();
        Console.WriteLine("REST API (127.0.0.1):");
        Console.WriteLine("  GET  /api/status");
        Console.WriteLine("  GET  /api/health");
        Console.WriteLine("  GET  /api/lobbies");
        Console.WriteLine("  GET  /api/steam/avatar/{steamId}");
        Console.WriteLine("  POST /api/login                { username, password, preferCode? }");
        Console.WriteLine("                                 preferCode=true면 모바일 승인 대신 코드 입력");
        Console.WriteLine("  GET  /api/login/{id}           -> waiting_code | waiting_confirmation | ...");
        Console.WriteLine("  POST /api/login/{id}/code      { code }");
        Console.WriteLine("  POST /api/login/{id}/cancel");
        Console.WriteLine("  POST /api/lobby/metadata");
        Console.WriteLine("  POST /api/reload");
    }
}
