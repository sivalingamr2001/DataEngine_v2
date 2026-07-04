# PERFORMANCE_AUDIT.md — DataEngine Solution

## Metadata Loading

- **Current**: `FieldMapperRepository.GetFieldMappersAsync(tableName, connection)` and `QueryRepository.GetQueryDefinitionAsync`/`GetAllQueryDefinitionsAsync` issue a live SQL query every time they're called. `TransactionService` caches per-table-name results only for the duration of a single `TransactionAsync` call (`mappersCache`, line 41), so a burst of concurrent transactions against the same table re-reads `de_field_mappers` from the database once per transaction.
- **Complexity**: O(1) query per unique table per transaction, but that constant is a full round trip; for the read path, `GetQueryDefinitionAsync` is one round trip per fetch call with zero reuse across requests.
- **Expected bottleneck**: `de_field_mappers`/`de_query_definitions` become hot, small, frequently-read tables that will show up as a top wait/lock contributor under load, purely because nothing outside a single request/transaction is cached.
- **Optimization plan**: L1 `IMemoryCache` (process-local, ~1–5 min TTL) + L2 Redis (cross-instance, longer TTL) per `CACHING_DESIGN.md`, invalidated on any write to the metadata tables (or via TTL-only if metadata changes are rare and an admin-triggered cache-bust is acceptable).

## Database Calls

- **Current**: `MySqlGetDataService.ExecuteAsync` makes two round trips per fetch: the paginated `SELECT ... LIMIT/OFFSET` and a separate `SELECT FOUND_ROWS()`. `TransactionService` makes one round trip per row for insert/update/delete, including per child record recursively (no batching).
- **Expected bottleneck**: Deeply nested `RenProps` payloads (the code allows up to 5 levels, `ProcessChildRecordsAsync` line 208) turn into a chain of sequential single-row round trips inside one open transaction — for wide payloads this is both slow (network round-trip-bound) and holds row/table locks for longer than necessary, increasing contention under concurrent load.
- **Optimization plan**:
  - Batch child-record inserts of the same table using multi-row `INSERT ... VALUES (...), (...), (...)` (still parameterized) where the operation type is uniformly `Insert` for a batch of siblings.
  - Replace `SQL_CALC_FOUND_ROWS` + second call with a single query using a `COUNT(*) OVER()` analytic column, cutting the read path from 2 round trips to 1.

## Reflection Usage

- **Current**: No heavy use of `System.Reflection` was found in the reviewed source; type coercion in `DataTypeConverter` is done via explicit `switch` on a `dataType` string plus `TryParse` calls, not reflection — this is actually good for performance (avoids reflection overhead on the hot path).
- **Expected bottleneck**: None from reflection specifically.

## Memory Allocations

- **Current**: `MySqlGetDataService.ExecuteAsync` materializes the entire page into a `List<Dictionary<string, object?>>` (line 99) — reasonable for paginated results bounded by `Count`/`MaxPageSize`, but there is no server-side enforcement that `query.Count` is capped at `DatabaseConfig.MaxPageSize` (`DatabaseConfig.cs` line 12) before it's used as the SQL `LIMIT` value — a caller can request an arbitrarily large page.
- **Expected bottleneck**: Unbounded `Count` values create large, unpredictable per-request allocations and large data transfer from the database; a single abusive/careless caller can degrade the service for everyone.
- **Optimization plan**: Clamp `limit = Math.Min(query.Count <= 0 ? 10 : query.Count, _config.MaxPageSize)` before building the SQL, and reject (400) requests that exceed the max rather than silently truncating, so the contract is explicit.

## Large Result Handling

- **Current**: No streaming path exists — every fetch fully buffers into memory before returning `FetchResult<T>` as a single JSON payload.
- **Optimization plan**: For genuinely large exports, consider a streaming response (`IAsyncEnumerable<T>` + `System.Text.Json` streaming serialization) as a separate endpoint, keeping the paginated endpoint as-is for interactive UI use.

## Async Usage

- **Current**: Async/await is used consistently and correctly throughout the reviewed code (`ExecuteReaderAsync`, `ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `OpenAsync`, all awaited with `CancellationToken` propagation). This is a genuine strength — no sync-over-async or blocking `.Result`/`.Wait()` calls were found.
- **Gap**: `CancellationToken` is accepted by controllers and threaded through the read path, but `TransactionService.TransactionAsync`'s `ct` parameter defaults to `default` in the interface and is not consistently checked against cancellation before starting nested recursive work — a cancelled request will still complete already-started child processing before the next await point notices cancellation. This is a minor efficiency issue, not a correctness one (the DB transaction still rolls back or commits atomically).

## Connection Pooling

- **Current**: `DatabaseConnectionFactory` creates a new `MySqlConnection`/`OracleConnection` object per logical request and relies on the underlying driver's internal connection pool (both MySqlConnector and Oracle.ManagedDataAccess pool by default at the connection-string level). No `DbDataSource`/`MySqlDataSource` (the modern, pre-configured factory object) is used, and there is no visibility (metrics) into pool size, in-use count, or wait time.
- **Expected bottleneck**: Under sustained high concurrency, pool exhaustion will manifest as slow `OpenAsync` calls that get silently retried by `DatabaseConnectionFactory`'s retry loop (up to `MaxRetryCount`, default 3) rather than surfaced as a specific "pool exhausted" signal, making the real cause harder to diagnose from logs alone.
- **Optimization plan**: Move to `MySqlDataSource`/`OracleDataSource` (builder-created, DI-registered singletons) and emit connection-pool metrics (see `OBSERVABILITY_DESIGN.md`) so pool pressure is visible before it becomes an outage.

## Query Optimization

- **Current**: `SqlGuardian.ValidateDirectQueryComplexity` limits JOINs (≤10), subqueries (≤5 by counting `SELECT` occurrences), and UNIONs (≤3) for the direct-query path — a reasonable guardrail against runaway ad hoc queries, though counting `SELECT` occurrences is a rough proxy for subquery count (it will also count `SELECT` used inside a CTE's own `WITH` clause, etc.) and can both under- and over-count in edge cases.
- **Stored queries** (via `QueryRepository`) have no complexity guardrail at all today — `SqlGuardian.ValidateReadOnlyQuery`/`ValidateDirectQuery` is only invoked in `MySqlGetDataService.ExecuteAsync` when `query.EnableDirectQueryExecution` is true; a registered query definition is trusted as-is.
- **Optimization plan**: Add `EXPLAIN`-based cost estimation (or a simpler proxy: enforce indexed WHERE columns for stored queries via metadata) as a longer-term enhancement; not urgent relative to the P0/P1 items.

## Summary Table

| Area | Current Complexity | Bottleneck Risk | Priority |
|---|---|---|---|
| Metadata loading | O(1) DB round trip per call, no cross-request cache | High under load | P2 (see Critical Issues) |
| Pagination | 2 round trips per fetch (`LIMIT`+`FOUND_ROWS`) | Medium | P3 |
| Unbounded page size | No server-side cap enforced | Medium–High | P1 (add alongside P1-2) |
| Nested child inserts | 1 round trip per row, sequential | Medium at deep nesting | P2 |
| Connection pooling visibility | None | Medium (diagnosability) | P2 |
| Async usage | Correct throughout | Low | — |
