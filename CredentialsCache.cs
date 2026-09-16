using System.Collections.Concurrent;

namespace ProxyHub;

/// <summary>
/// 通用凭据缓存：TTL + in-flight 去重（并发只触发一次底层刷新）。
/// 对应上游 credentials.js。
/// </summary>
public sealed class CredentialsCache
{
    private sealed record Entry(object Value, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new();

    public bool Has(string key) =>
        _entries.TryGetValue(key, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow;

    public T Set<T>(string key, T value, DateTimeOffset expiresAt)
    {
        _entries[key] = new Entry(value!, expiresAt);
        return value;
    }

    /// <summary>命中缓存直接返回；未命中时并发去重地执行 loader 并写入缓存。loader 抛错不缓存，下次重试。</summary>
    public async Task<T> GetAsync<T>(string key, Func<Task<(T Value, DateTimeOffset ExpiresAt)>> loader)
    {
        if (_entries.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow)
            return (T)hit.Value;

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object>>(
            async () =>
            {
                var (value, expiresAt) = await loader();
                _entries[key] = new Entry(value!, expiresAt);
                return value!;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return (T)await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void Invalidate(string key)
    {
        _entries.TryRemove(key, out _);
        _inflight.TryRemove(key, out _);
    }
}
