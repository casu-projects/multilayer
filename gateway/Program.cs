namespace CasuMpGateway;

internal static class Program
{
    private static void Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "gateway.json";
        GatewayConfig config = GatewayConfig.Load(configPath);

        Logger.Info($"구성 로드 완료: {configPath}");
        Logger.Info($"제어 채널: {config.OrchestratorAddr}");
        Logger.Info($"직접연결 포트: {config.DirectListenPort}");

        using var cts = new CancellationTokenSource();

        var core = new GatewayCore(config);
        Logger.Init("gateway");
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
            Logger.Info("Steam 비활성화 (steamEnabled=false).");
        }

        var control = new ControlChannel(config, core);
        _ = control.RunAsync(cts.Token);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Logger.Info("종료 신호 수신.");
        };

        while (!cts.IsCancellationRequested)
        {
            direct.PollEvents();
            steam?.PollEvents();
            core.Tick();
            Thread.Sleep(10);
        }

        Logger.Info("종료 중...");
        direct.Stop();
        steam?.Stop();
    }
}
