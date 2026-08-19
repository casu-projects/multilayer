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
        // 오케스트레이터 종료 신호 -> 메인 루프 종료 (전 세션 Kick은 ApplyShutdown이 수행)
        core.ShutdownRequested += () => cts.Cancel();

        var direct = new DirectIpAdapter(config, core);
        direct.Start();

        SteamLobbyAdapter? steam = null;
        if (config.SteamEnabled)
        {
            steam = new SteamLobbyAdapter(config, core);
            steam.Start();
            core.OnLobbyMetadata = steam.UpdateLobbyMetadata;
            // 진단 연동 - 오케스트레이터 LOBBY_STATUS 요청에 현재 로비 상태 응답
            core.LobbyStatusProvider = () => new GatewayCore.LobbyStatusSnapshot
            {
                SteamEnabled = true,
                State = steam.LobbyState.ToString(),
                LobbyId = steam.LobbyId,
                LoggedOn = steam.LobbyLoggedOn,
                AuthInfoReceived = core.AuthInfoReceived,
                SteamApiInitialized = steam.SteamApiInitialized,
            };
        }
        else
        {
            Logger.Info("Steam 비활성화 (steamEnabled=false).");
            core.LobbyStatusProvider = () => new GatewayCore.LobbyStatusSnapshot
            {
                SteamEnabled = false,
                State = "Disabled",
                AuthInfoReceived = core.AuthInfoReceived,
                SteamApiInitialized = false,
            };
        }

        var control = new ControlChannel(config, core);
        _ = control.RunAsync(cts.Token);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Logger.Info("종료 신호 수신.");
        };

        // 메인 루프 - 모든 NetManager 접근은 이 스레드에서만 (LiteNetLib 스레드 안전성)
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
