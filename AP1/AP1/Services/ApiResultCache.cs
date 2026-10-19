using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace AP1.Services;

/// <summary>Shares successfully loaded API results across Blazor components and requests.</summary>
public sealed class ApiResultCache(IMemoryCache memoryCache)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _loads = new();

    public async Task<T> GetOrLoadAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> load,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var cacheKey = CacheKey<T>(key);
        if (memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
            return cached;

        // A caller leaving a tab must not cancel a load that another caller is using.
        var pending = _loads.GetOrAdd(cacheKey, _ => CreateLoad(cacheKey, lifetime, load));
        return (T)await pending.Value.WaitAsync(cancellationToken);
    }

    public async Task<T> RefreshAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> load,
        CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var cacheKey = CacheKey<T>(key);
        if (_loads.TryGetValue(cacheKey, out var pending))
        {
            try { await pending.Value.WaitAsync(cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }

        var value = await load(cancellationToken);
        memoryCache.Set(cacheKey, value, lifetime);
        return value;
    }

    private static string CacheKey<T>(string key) => $"{typeof(T).FullName}:{key}";

    private Lazy<Task<object>> CreateLoad<T>(
        string cacheKey,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> load) where T : notnull
    {
        Lazy<Task<object>>? pending = null;
        pending = new Lazy<Task<object>>(async () =>
        {
            try
            {
                // The cache may have filled between the first lookup and this load.
                if (memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
                    return cached;

                var value = await load(CancellationToken.None);
                memoryCache.Set(cacheKey, value, lifetime);
                return value;
            }
            finally
            {
                _loads.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(cacheKey, pending!));
            }
        });
        return pending;
    }

    public static TimeSpan LifetimeForSeason(int year) =>
        year == DateTime.UtcNow.Year ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24);
}
