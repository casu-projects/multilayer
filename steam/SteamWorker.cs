using Microsoft.Extensions.Hosting;

namespace CasuMpSteam;

public sealed class SteamWorker : BackgroundService
{
    private readonly SteamService _service;

    public SteamWorker(SteamService service) => _service = service;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _service.Start();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _service.Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SteamLog.Info($"Steam 서비스 오류: {ex.Message}");
        }
        finally
        {
            _service.Stop();
        }
    }
}
