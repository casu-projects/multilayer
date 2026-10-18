using System.Collections.Concurrent;

namespace CasuMpServer;

public sealed class RequestRegistry<T> where T : class
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<T>> _pending = new();

    public TaskCompletionSource<T> Register(string requestId)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = source;
        return source;
    }

    public bool Resolve(string requestId, T response)
    {
        if (!_pending.TryRemove(requestId, out TaskCompletionSource<T>? source)) return false;
        source.TrySetResult(response);
        return true;
    }

    public void Remove(string requestId) => _pending.TryRemove(requestId, out _);

    public void FailAll(string reason)
    {
        foreach ((string requestId, TaskCompletionSource<T> source) in _pending.ToArray())
        {
            if (!_pending.TryRemove(requestId, out _)) continue;
            source.TrySetCanceled();
        }
    }
}
