using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace CasuMpServer;

internal sealed class SteamApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Channel<LobbyMetadataPayload> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public SteamApiClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };

        _queue = Channel.CreateBounded<LobbyMetadataPayload>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Run(RunAsync);
    }

    public void Post(LobbyMetadataPayload payload) => _queue.Writer.TryWrite(payload);

    public async Task<string?> GetAvatarAsync(ulong steamId)
    {
        try
        {
            using var response = await _http.GetAsync($"api/steam/avatar/{steamId}", _cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cts.Token));
            return doc.RootElement.TryGetProperty("avatarFull", out JsonElement avatar)
                ? avatar.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunAsync()
    {
        int failures = 0;
        try
        {
            await foreach (LobbyMetadataPayload payload in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var response = await _http.PostAsJsonAsync("api/lobby/metadata", payload, _cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            failures = 0;
                            break;
                        }
                        failures++;
                        if (failures == 1 || failures % 12 == 0)
                        {
                            Log.Info($"Steam 메타데이터 푸시 실패: HTTP {(int)response.StatusCode}");
                        }
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        if (failures == 1 || failures % 12 == 0)
                        {
                            Log.Info($"Steam 메타데이터 푸시 실패: {ex.Message}");
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _http.Dispose();
        _cts.Dispose();
    }
}
