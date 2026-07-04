# RESILIENCY_DESIGN.md

## Current State

`DatabaseConnectionFactory.CreateAndOpenConnectionAsync` (lines 61–111) is the *only* place with retry logic today, and it only covers `OpenAsync`. Once a connection is open, `ExecuteReaderAsync`/`ExecuteNonQueryAsync`/`ExecuteScalarAsync` calls in `MySqlGetDataService` and `TransactionService` have no retry, no circuit breaker, no explicit command timeout, and no fallback.

## Polly Policy Set

```csharp
namespace DataEngine.Core.Resiliency;

public static class DataEnginePolicies
{
    public static IAsyncPolicy<T> BuildQueryPolicy<T>(IDbProviderStrategy strategy, ILogger logger)
    {
        var retry = Policy<T>
            .Handle<Exception>(ex => strategy.IsTransient(ex))
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1))
                                                   + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100)), // jitter
                onRetry: (outcome, delay, attempt, _) =>
                    logger.LogWarning(outcome.Exception, "Transient DB error, retry {Attempt} after {Delay}ms", attempt, delay.TotalMilliseconds));

        var circuitBreaker = Policy<T>
            .Handle<Exception>(ex => strategy.IsTransient(ex))
            .AdvancedCircuitBreakerAsync(
                failureThreshold: 0.5,          // open if 50% of calls fail...
                samplingDuration: TimeSpan.FromSeconds(30),
                minimumThroughput: 10,          // ...over at least 10 calls in the sampling window
                durationOfBreak: TimeSpan.FromSeconds(15),
                onBreak: (outcome, breakDelay) => logger.LogError(outcome.Exception, "Circuit OPEN for {Delay}s", breakDelay.TotalSeconds),
                onReset: () => logger.LogInformation("Circuit RESET"));

        var timeout = Policy.TimeoutAsync<T>(TimeSpan.FromSeconds(10), TimeoutStrategy.Pessimistic);

        return Policy.WrapAsync(retry, circuitBreaker, timeout);
    }

    /// <summary>Fallback for read-only queries: return an empty/degraded result rather than a hard 500 when the circuit is open.</summary>
    public static IAsyncPolicy<FetchResult<T>> BuildReadFallbackPolicy<T>(ILogger logger)
    {
        return Policy<FetchResult<T>>
            .Handle<BrokenCircuitException>()
            .FallbackAsync(
                fallbackValue: new FetchResult<T> { Success = false, Message = "Data service temporarily unavailable. Please retry shortly." },
                onFallbackAsync: (_, _) => { logger.LogWarning("Serving fallback result — circuit open."); return Task.CompletedTask; });
    }
}
```

## Deadlock Retry Handling

Deadlocks (`MySqlException.Number == 1213` for MySQL, `SqlException.Number == 1205` for SQL Server, ORA-00060 for Oracle) are, by definition, safe to retry — one of the two transactions was rolled back specifically so the other could proceed. `IDbProviderStrategy.IsTransient` (see `MULTI_DATABASE_DESIGN.md`) already includes these codes, so the same retry policy above handles deadlocks without special-casing — the important discipline is that the **entire transaction** (`TransactionAsync`, including a fresh `BeginTransactionAsync`) must be retried, not just the single statement that deadlocked, since the transaction's earlier work was rolled back too.

```csharp
// Wrapping the whole TransactionAsync body, not just one command:
public async Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken ct = default)
{
    var policy = DataEnginePolicies.BuildQueryPolicy<TransactionResult>(_strategy, _logger);

    return await policy.ExecuteAsync(async () =>
    {
        await using DbConnection connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // ... existing logic ...
            await transaction.CommitAsync(ct);
            return successResult;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            if (_strategy.IsTransient(ex)) throw; // let Polly retry with a brand-new connection/transaction
            return new TransactionResult { Success = false, TransactionId = transactionId, Message = "Transaction failed." };
        }
    });
}
```

Note the idempotency check (`IDEMPOTENCY_DESIGN.md`) must run **once**, outside the retried block — otherwise a retried attempt would see its own in-progress claim and incorrectly report a conflict. Structure: claim once → run the Polly-wrapped execution → complete/fail the claim once, based on the final outcome.

## Command Timeouts

Every `DbCommand` created in `MySqlGetDataService`/`TransactionService` should have an explicit `CommandTimeout` (currently unset, meaning driver defaults apply — typically 30s, but not deliberately chosen). Set from configuration (`DatabaseConfig.CommandTimeoutSeconds`, a new field) so it is a conscious, tunable value rather than an implicit default:

```csharp
command.CommandTimeout = _config.CommandTimeoutSeconds; // e.g. 15 for reads, higher for large transaction graphs
```

## Fallback Policies

- **Reads**: on circuit-open, return a clear "temporarily unavailable" response (as shown above) rather than letting requests queue up against a database that is already struggling.
- **Writes**: no silent fallback is appropriate for a mutation (never fabricate a fake "success"); on circuit-open, fail fast with a specific error so the caller's own retry/backoff logic (ideally using the idempotency key) can back off appropriately.

## Summary of Where Each Policy Applies

| Operation | Retry | Circuit Breaker | Timeout | Fallback |
|---|---|---|---|---|
| Metadata reads (`FieldMapper`, `QueryDefinition`) | Yes | Yes | Yes | Serve last-good cached value if available |
| Fetch/read queries | Yes | Yes | Yes | Degraded "unavailable" response |
| Transaction writes | Yes (whole-transaction retry only, deadlock/transient) | Yes | Yes | Fail fast, no fabricated success |
| Connection open | Yes (already implemented in `DatabaseConnectionFactory`) | — | Implicit via retry loop | — |
