using CasuMp.Protocol;
using System.Collections.Concurrent;

namespace CasuMpServer;

public sealed class PlayerDetailRegistry
{
    public const int MaxPending = 256;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PlayerDetailResponse>> _pending = new();
    private readonly object _registerGate = new();

    public TaskCompletionSource<PlayerDetailResponse>? Register(string requestId)
    {
        lock (_registerGate)
        {
            if (_pending.Count >= MaxPending) return null;
            var source = new TaskCompletionSource<PlayerDetailResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, source)) return null;
            return source;
        }
    }

    public bool Resolve(PlayerDetailResponse response)
    {
        if (!_pending.TryRemove(response.RequestId, out TaskCompletionSource<PlayerDetailResponse>? source))
            return false;
        source.TrySetResult(response);
        return true;
    }

    public void Remove(string requestId) => _pending.TryRemove(requestId, out _);

    public void FailAll(string reason)
    {
        foreach ((string requestId, TaskCompletionSource<PlayerDetailResponse> source) in _pending.ToArray())
        {
            if (!_pending.TryRemove(requestId, out _)) continue;
            source.TrySetResult(new PlayerDetailResponse { RequestId = requestId, Found = false, Error = reason });
        }
    }
}
