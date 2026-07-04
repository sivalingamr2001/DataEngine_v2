# CACHING_DESIGN.md

## Current State

No caching exists outside a single-transaction dictionary (`mappersCache` in `TransactionService.cs`, line 41) that only survives one `TransactionAsync` call. `FieldMapperRepository.GetFieldMappersAsync` and `QueryRepository.GetQueryDefinitionAsync`/`GetAllQueryDefinitionsAsync` hit the database on every call.

## Two-Tier Design

- **L1 — `IMemoryCache`**: process-local, sub-millisecond, first line of defense; short TTL (a stale L1 entry self-heals quickly).
- **L2 — Redis**: shared across all API instances, avoids a cold L1 on every instance hitting the DB simultaneously after a deploy/restart; longer TTL, explicitly invalidated on writes to the underlying metadata tables.

```csharp
namespace DataEngine.Core.Caching;

public interface ITieredCache
{
    Task<T?> GetOrCreateAsync<T>(string key, TimeSpan l1Ttl, TimeSpan l2Ttl, Func<Task<T>> factory, CancellationToken ct = default) where T : class;
    Task InvalidateAsync(string key, CancellationToken ct = default);
}

public sealed class TieredCache(IMemoryCache l1, IConnectionMultiplexer redis, ILogger<TieredCache> logger) : ITieredCache
{
    public async Task<T?> GetOrCreateAsync<T>(string key, TimeSpan l1Ttl, TimeSpan l2Ttl, Func<Task<T>> factory, CancellationToken ct = default) where T : class
    {
        if (l1.TryGetValue(key, out T? cached))
        {
            DataEngineMetrics.CacheHits.Add(1, new("tier", "L1"));
            return cached;
        }

        var db = redis.GetDatabase();
        var redisValue = await db.StringGetAsync(key);
        if (redisValue.HasValue)
        {
            DataEngineMetrics.CacheHits.Add(1, new("tier", "L2"));
            var deserialized = JsonSerializer.Deserialize<T>(redisValue!);
            l1.Set(key, deserialized, l1Ttl);
            return deserialized;
        }

        DataEngineMetrics.CacheMisses.Add(1);
        var value = await factory();
        if (value is not null)
        {
            await db.StringSetAsync(key, JsonSerializer.Serialize(value), l2Ttl);
            l1.Set(key, value, l1Ttl);
        }
        return value;
    }

    public async Task InvalidateAsync(string key, CancellationToken ct = default)
    {
        l1.Remove(key);
        await redis.GetDatabase().KeyDeleteAsync(key);
    }
}
```

## What Gets Cached

| Data | Key pattern | L1 TTL | L2 TTL | Invalidation |
|---|---|---|---|---|
| Field mappers per table | `de:fm:{tableName}` | 60s | 15 min | On any admin write to `de_field_mappers` for that table |
| Query definitions (by id/key) | `de:qd:id:{id}` / `de:qd:key:{key}` | 60s | 15 min | On admin write to `de_query_definitions` |
| Allowed table names (schema catalog, P0-1) | `de:schema-catalog:allowed-tables` | 60s | 15 min | On any `de_field_mappers` table-list change |
| Reference/lookup data (if introduced later) | `de:ref:{entity}` | 30s | 10 min | TTL-only unless writes are frequent |
| Query *results* | Not cached by default — see note below | — | — | — |

**Query result caching is intentionally excluded from the default design.** Caching arbitrary `SELECT` result sets keyed by query+parameters is attractive for performance but risks serving stale or (if the cache key is derived incorrectly) *cross-tenant* data; if it's added later, the cache key must include every parameter and the requesting principal's tenant/permission scope, and it should be opt-in per registered query definition (a `CacheableSeconds` column on `de_query_definitions`), not blanket.

## Wiring into Repositories

```csharp
public sealed class FieldMapperRepository(ITieredCache cache, DatabaseConnectionFactory connectionFactory) : IFieldMapperRepository
{
    public async Task<List<FieldMapper>> GetFieldMappersAsync(string tableName, IDbConnection connection)
    {
        var result = await cache.GetOrCreateAsync(
            key: $"de:fm:{tableName}",
            l1Ttl: TimeSpan.FromSeconds(60),
            l2Ttl: TimeSpan.FromMinutes(15),
            factory: () => LoadFromDatabaseAsync(tableName, connection));

        return result ?? [];
    }

    private async Task<List<FieldMapper>> LoadFromDatabaseAsync(string tableName, IDbConnection connection)
    {
        // existing parameterized query logic from the current implementation
        ...
    }
}
```

## Invalidation Strategy

- Preferred: explicit invalidation from whatever admin/config endpoint edits `de_field_mappers`/`de_query_definitions` (call `ITieredCache.InvalidateAsync` for the affected key(s) as part of that write transaction).
- Fallback/defense-in-depth: TTL expiry (15 minutes on L2) bounds the worst-case staleness even if an invalidation call is missed, which is an acceptable trade-off for metadata that changes rarely relative to read volume.
- On deploy/restart, L1 is naturally empty; L2 (Redis) survives the restart, so the "cold cache thundering herd against the DB" scenario is limited to the first instance to start after a Redis flush, not every instance after every deploy.
