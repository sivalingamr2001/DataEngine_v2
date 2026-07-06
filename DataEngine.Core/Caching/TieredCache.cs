using DataEngine.ReaderService.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace DataEngine.Core.Caching;

public interface ITieredCache
{
    // FIX: Moved optional parameter 'option' after required 'factory' parameter
    Task<T?> GetOrCreateAsync<T>(
        string key,
        TimeSpan l1Ttl,
        TimeSpan l2Ttl,
        Func<Task<T?>> factory,
        CacheOption option = CacheOption.IMemory,
        CancellationToken ct = default) where T : class;

    // FIX: Added default value to InvalidateAsync to support seamless default calls
    Task InvalidateAsync(
        string key,
        CacheOption option = CacheOption.IMemory,
        CancellationToken ct = default);
}

public sealed class TieredCache : ITieredCache
{
    private readonly IMemoryCache _l1;
    private readonly IConnectionMultiplexer _redisMultiplexer;
    private readonly ILogger<TieredCache> _logger;

    public TieredCache(IMemoryCache l1, IConnectionMultiplexer redisMultiplexer, ILogger<TieredCache> logger)
    {
        _l1 = l1 ?? throw new ArgumentNullException(nameof(l1));
        _redisMultiplexer = redisMultiplexer ?? throw new ArgumentNullException(nameof(redisMultiplexer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // FIX: Match interface parameter order precisely
    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        TimeSpan l1Ttl,
        TimeSpan l2Ttl,
        Func<Task<T?>> factory,
        CacheOption option = CacheOption.IMemory,
        CancellationToken ct = default) where T : class
    {
        // 1. STRATEGY: Read from L1 Memory Cache if requested
        if (option == CacheOption.IMemory)
        {
            if (_l1.TryGetValue(key, out T? cached))
            {
                return cached;
            }
        }

        // 2. STRATEGY: Read from Redis if requested
        if (option == CacheOption.Redis && TryGetRedisDatabase(out var db))
        {
            try
            {
                var redisValue = await db.StringGetAsync(key);
                if (redisValue.HasValue)
                {
                    var deserialized = JsonSerializer.Deserialize<T>(redisValue.ToString());

                    // Populate L1 cache as a fast fallback loop
                    _l1.Set(key, deserialized, l1Ttl);
                    return deserialized;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis cache lookup failed for key {Key}; falling back to database source.", key);
            }
        }

        // 3. CACHE MISS: Fetch data from the database factory
        var value = await factory();
        if (value is not null)
        {
            // 4. STRATEGY: Save to the requested cache target
            if (option == CacheOption.Redis && TryGetRedisDatabase(out var writeDb))
            {
                try
                {
                    await writeDb.StringSetAsync(key, JsonSerializer.Serialize(value), l2Ttl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Redis cache write failed for key {Key}.", key);
                }
            }

            // Always back up into local memory cache for maximum availability
            _l1.Set(key, value, l1Ttl);
        }

        return value;
    }

    // FIX: Match interface parameter defaults precisely
    public async Task InvalidateAsync(
        string key,
        CacheOption option = CacheOption.IMemory,
        CancellationToken ct = default)
    {
        _l1.Remove(key);

        if (option == CacheOption.Redis && TryGetRedisDatabase(out var db))
        {
            try
            {
                await db.KeyDeleteAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis cache invalidation failed for key {Key}.", key);
            }
        }
    }

    private bool TryGetRedisDatabase(out IDatabase db)
    {
        try
        {
            if (_redisMultiplexer.IsConnected)
            {
                db = _redisMultiplexer.GetDatabase();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Redis connection pool is temporarily unavailable.");
        }

        db = default!;
        return false;
    }
}
