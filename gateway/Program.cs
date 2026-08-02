namespace CasuMpGateway;

internal static class Program
{
    private static void Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "gateway.json";
        GatewayConfig config = GatewayConfig.Load(configPath);

        Log.Info($"구성 로드 완료: {configPath}");
        Log.Info($"제어 채널: {config.OrchestratorAddr} (오케스트레이터 대기)");
        Log.Info($"직접연결 포트: {config.DirectListenPort}");

        using var cts = new CancellationTokenSource();

        var core = new GatewayCore(config);
        Log.Core = core;
        // 오케스트레이터 종료 신호 → 메인 루프 종료 (전 세션 Kick은 ApplyShutdown이 수행)
        core.ShutdownRequested += () => cts.Cancel();

        var direct = new DirectIpAdapter(config, core);
        direct.Start();

        SteamLobbyAdapter? steam = null;
        if (config.SteamEnabled)
        {
            steam = new SteamLobbyAdapter(config, core);
            steam.Start();
            core.OnLobbyMetadata = steam.UpdateLobbyMetadata;
        }
        else
        {
            Log.Info("Steam 비활성화 (steamEnabled=false).");
        }

        var control = new ControlChannel(config, core);
        _ = control.RunAsync(cts.Token);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log.Info("종료 신호 수신.");
        };

        // 메인 루프 — 모든 NetManager 접근은 이 스레드에서만 (LiteNetLib 스레드 안전성).
        while (!cts.IsCancellationRequested)
        {
            direct.PollEvents();
            steam?.PollEvents();
            core.Tick();
            Thread.Sleep(10);
        }

        Log.Info("종료 중...");
        direct.Stop();
        steam?.Stop();
    }
}
