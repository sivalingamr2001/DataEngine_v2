# IDEMPOTENCY_DESIGN.md — Exactly-Once Transaction Execution

## Problem

`TransactionService.TransactionAsync` (`DataEngine.TransactionService/Services/TransactionService.cs`, lines 23–82) accepts or generates a `TransactionId` and logs it, but never checks whether that ID has been processed before. Any retry — client timeout, load balancer retry, double-tap in a UI — re-runs the entire insert/update/delete graph.

## Schema Design

```sql
CREATE TABLE de_idempotency_keys (
    transaction_id      VARCHAR(100)  NOT NULL PRIMARY KEY,
    entity_name         VARCHAR(128)  NOT NULL,
    request_hash        CHAR(64)      NOT NULL,          -- SHA-256 of the normalized request payload
    status               VARCHAR(20)  NOT NULL,          -- Processing | Completed | Failed
    result_json          TEXT         NULL,               -- serialized TransactionResult, once known
    created_at           DATETIME(6)  NOT NULL,
    completed_at         DATETIME(6)  NULL,
    expires_at            DATETIME(6) NOT NULL,           -- created_at + retention window (e.g. 7 days)

    INDEX ix_de_idem_expires (expires_at)
);
```

`request_hash` guards against a **reused** `TransactionId` being sent with a **different** payload (a client bug), which should be rejected rather than silently returning the old result.

## Service Design

```csharp
namespace DataEngine.Core.Idempotency;

public enum IdempotencyStatus { New, InProgress, Completed, Conflict }

public sealed record IdempotencyClaim(IdempotencyStatus Status, string? CachedResultJson);

public interface IIdempotencyService
{
    /// <summary>
    /// Attempts to claim a transaction id for exclusive processing.
    /// Returns Completed with the cached result if already done, Conflict if the id was reused with a different payload,
    /// InProgress if another request is currently processing it, or New if this caller should proceed.
    /// </summary>
    Task<IdempotencyClaim> TryClaimAsync(string transactionId, string entityName, string requestHash, CancellationToken ct);

    Task CompleteAsync(string transactionId, string resultJson, CancellationToken ct);

    Task FailAsync(string transactionId, CancellationToken ct);
}
```

### Redis-first, DB-fallback implementation

```csharp
public sealed class IdempotencyService(
    IConnectionMultiplexer redis,
    IIdempotencyRepository dbFallback,
    ILogger<IdempotencyService> logger) : IIdempotencyService
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromDays(7);

    public async Task<IdempotencyClaim> TryClaimAsync(string transactionId, string entityName, string requestHash, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        string key = $"de:idem:{transactionId}";

        // Fast path: Redis-native compare-and-set claim.
        var claimed = await db.StringSetAsync(key, requestHash, LockTtl, When.NotExists);
        if (claimed)
        {
            // We own this id now — also persist to the DB so retries survive a Redis flush/outage.
            await dbFallback.InsertInProgressAsync(transactionId, entityName, requestHash, ct);
            return new IdempotencyClaim(IdempotencyStatus.New, null);
        }

        var existingHash = await db.StringGetAsync(key);
        if (existingHash.HasValue && existingHash != requestHash)
        {
            return new IdempotencyClaim(IdempotencyStatus.Conflict, null);
        }

        // Someone already claimed it with the same payload — check the DB for a final result.
        var record = await dbFallback.GetAsync(transactionId, ct);
        if (record is null)
        {
            // Redis says claimed but DB fallback missing (edge case, e.g. crash between claim and insert) — treat as in-progress.
            return new IdempotencyClaim(IdempotencyStatus.InProgress, null);
        }

        if (record.RequestHash != requestHash)
        {
            return new IdempotencyClaim(IdempotencyStatus.Conflict, null);
        }

        return record.Status switch
        {
            "Completed" => new IdempotencyClaim(IdempotencyStatus.Completed, record.ResultJson),
            "Failed" => new IdempotencyClaim(IdempotencyStatus.New, null), // allow retry of a failed attempt
            _ => new IdempotencyClaim(IdempotencyStatus.InProgress, null)
        };
    }

    public async Task CompleteAsync(string transactionId, string resultJson, CancellationToken ct)
    {
        await dbFallback.MarkCompletedAsync(transactionId, resultJson, ct);
        var db = redis.GetDatabase();
        await db.StringSetAsync($"de:idem:{transactionId}:result", resultJson, ResultTtl);
    }

    public async Task FailAsync(string transactionId, CancellationToken ct)
    {
        await dbFallback.MarkFailedAsync(transactionId, ct);
        await redis.GetDatabase().KeyDeleteAsync($"de:idem:{transactionId}");
    }
}
```

### Repository design (DB fallback)

```csharp
public interface IIdempotencyRepository
{
    Task InsertInProgressAsync(string transactionId, string entityName, string requestHash, CancellationToken ct);
    Task<IdempotencyRecord?> GetAsync(string transactionId, CancellationToken ct);
    Task MarkCompletedAsync(string transactionId, string resultJson, CancellationToken ct);
    Task MarkFailedAsync(string transactionId, CancellationToken ct);
}

public sealed class IdempotencyRepository(DatabaseConnectionFactory connectionFactory) : IIdempotencyRepository
{
    public async Task InsertInProgressAsync(string transactionId, string entityName, string requestHash, CancellationToken ct)
    {
        await using var conn = await connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO de_idempotency_keys (transaction_id, entity_name, request_hash, status, created_at, expires_at)
            VALUES (@id, @entity, @hash, 'Processing', UTC_TIMESTAMP(6), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 7 DAY))
            ON DUPLICATE KEY UPDATE transaction_id = transaction_id"; // no-op on conflict; Redis already gated the race

        AddParam(cmd, "@id", transactionId);
        AddParam(cmd, "@entity", entityName);
        AddParam(cmd, "@hash", requestHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // GetAsync / MarkCompletedAsync / MarkFailedAsync: straightforward parameterized SELECT/UPDATE, omitted for brevity.
    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
```

## Integration into `TransactionService.TransactionAsync`

```csharp
public async Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken ct = default)
{
    if (request == null || string.IsNullOrWhiteSpace(request.TransactionEntityName))
        return new TransactionResult { Success = false, Message = "Invalid transaction payload." };

    string transactionId = string.IsNullOrWhiteSpace(request.TransactionId) ? Guid.NewGuid().ToString() : request.TransactionId;
    string requestHash = ComputeRequestHash(request);

    var claim = await _idempotencyService.TryClaimAsync(transactionId, request.TransactionEntityName, requestHash, ct);
    switch (claim.Status)
    {
        case IdempotencyStatus.Completed:
            return JsonSerializer.Deserialize<TransactionResult>(claim.CachedResultJson!)!;
        case IdempotencyStatus.Conflict:
            return new TransactionResult { Success = false, TransactionId = transactionId, Message = "TransactionId reused with a different payload." };
        case IdempotencyStatus.InProgress:
            return new TransactionResult { Success = false, TransactionId = transactionId, Message = "This transaction is already being processed." };
    }

    try
    {
        // ... existing insert/update/delete/child logic, unchanged ...
        await transaction.CommitAsync(ct);
        var result = new TransactionResult { Success = true, TransactionId = transactionId, /* ... */ };
        await _idempotencyService.CompleteAsync(transactionId, JsonSerializer.Serialize(result), ct);
        return result;
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync(ct);
        await _idempotencyService.FailAsync(transactionId, ct);
        return new TransactionResult { Success = false, TransactionId = transactionId, Message = $"Transaction rolled back: {ex.Message}" };
    }
}
```

## Sequence Diagram (text form)

```
Client            API              IdempotencyService      Redis          DB (de_idempotency_keys)   Target Tables
  │  POST /execute  │                       │                  │                   │                       │
  │────────────────►│                       │                  │                   │                       │
  │                 │  TryClaimAsync(txId)  │                  │                   │                       │
  │                 │──────────────────────►│  SETNX de:idem:  │                   │                       │
  │                 │                       │─────────────────►│                   │                       │
  │                 │                       │◄─────────────────│ claimed=true      │                       │
  │                 │                       │  InsertInProgress│                   │                       │
  │                 │                       │─────────────────────────────────────►│                       │
  │                 │◄──────────────────────│ New               │                  │                       │
  │                 │  ... run INSERT/UPDATE/DELETE inside DB transaction ...       │──────────────────────►│
  │                 │  CompleteAsync(txId, resultJson)                              │                       │
  │                 │──────────────────────►│  SET result TTL7d │                  │                       │
  │                 │                       │─────────────────►│                   │                       │
  │                 │                       │  MarkCompleted   │                   │                       │
  │                 │                       │─────────────────────────────────────►│                       │
  │◄────────────────│  200 OK (result)      │                  │                   │                       │
  │  (retry) POST   │                       │                  │                   │                       │
  │────────────────►│  TryClaimAsync(txId)  │                  │                   │                       │
  │                 │──────────────────────►│  SETNX fails (exists, same hash)      │                       │
  │                 │◄──────────────────────│ Completed + cachedResultJson          │                       │
  │◄────────────────│  200 OK (same result, no re-execution)    │                  │                       │
```

## Distributed Deployment Support

- The Redis `SETNX` claim is the cross-instance mutual-exclusion primitive; any API instance can safely receive the retry because the claim lives outside process memory.
- The DB fallback table ensures correctness even if Redis is unavailable or flushed — `TryClaimAsync` degrades to DB-only claim logic (the DB `INSERT ... ON DUPLICATE KEY` acts as the compare-and-set when Redis is down); this should be implemented as an explicit fallback branch, not shown in full above for brevity.
- Expired keys (`expires_at`) should be purged by a scheduled job; a completed transaction's idempotency guarantee only needs to hold for a bounded retry window (a week is a reasonable default, tune to the client retry policy).
