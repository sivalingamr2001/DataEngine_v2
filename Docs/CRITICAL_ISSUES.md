# CRITICAL_ISSUES.md — DataEngine Solution

Issues are ranked P0 (must fix before any production traffic) → P3 (enhancement). Each entry cites the concrete file/line evidence found in the uploaded solution.

---

## P0 — Must Fix Before Production

### P0-1. Unvalidated table/column identifiers interpolated into SQL (write path)

- **File**: `DataEngine.TransactionService/Services/TransactionService.cs`, lines 100–193, 213–280 (`ProcessInsertAsync`, `ProcessUpdateAsync`, `ProcessDeleteOperationsAsync`); table name originates from `TransactionRequest.TransactionEntityName` and the dictionary keys of `RenProps`/`DelProps` (`DataEngine.TransactionService/Domain/TransactionRequest.cs` lines 16–30).
- **Root cause**: `tableName` (and each `mapper.ColumnName`) is wrapped only in backticks and concatenated directly into the SQL string (`$"INSERT INTO \`{tableName}\` (...)`, `$"UPDATE \`{tableName}\` SET ..."`, `$"DELETE FROM \`{tableName}\` WHERE ..."`). Table/column identifiers cannot be passed as ADO.NET parameters, so this *requires* an application-level allow-list check — none exists. `FieldMapperRepository.GetFieldMappersAsync` (parameterized, safe) simply returns an **empty list** when `tableName` doesn't match any registered mapping; the calling code does not treat an empty mapper list as an error, it proceeds to build `INSERT INTO \`{arbitraryName}\` (\`createdon\`, \`createdby\`) VALUES (...)` regardless.
- **Impact**: Any caller of `POST /api/transaction/execute` can supply `TransactionEntityName` (or a `RenProps`/`DelProps` key) equal to an arbitrary string. Because the backtick-quoting has no escaping of embedded backticks, a value such as `` users` (col) VALUES (1) -- `` breaks out of the identifier context. Depending on server settings (e.g. MySqlConnector allows multiple statements per command by default unless `AllowUserVariables`/`AllowBatch` restrictions are configured), this can escalate to full multi-statement SQL injection: arbitrary INSERT/UPDATE/DELETE against **any** table the DB user can reach, not just tables registered in `de_field_mappers`. At minimum, even without stacked queries, it allows writing rows into any table the application's DB credential has access to, bypassing the entire field-mapper permission model.
- **Recommendation**: Add a `SchemaCatalogService` that loads the distinct set of `table_name` values that exist in `de_field_mappers` (cached) and reject any `TransactionEntityName`/`RenProps`/`DelProps` key that is not an exact match, *before* any SQL string is built. Additionally validate identifiers against a strict regex (`^[A-Za-z_][A-Za-z0-9_]{0,63}$`) as defense in depth, and fail (not silently insert with only 2 columns) when the resulting mapper list is empty.
- **Estimated Effort**: 1–2 days (service + tests + wiring into both Insert/Update/Delete/child-recursion paths).

```csharp
// DataEngine.Core/Security/ITableNameValidator.cs
namespace DataEngine.Core.Security;

public interface ITableNameValidator
{
    /// <summary>Throws SqlValidationException if tableName is not a registered, safe identifier.</summary>
    Task EnsureAllowedAsync(string tableName, CancellationToken ct = default);
}

// DataEngine.Core/Security/TableNameValidator.cs
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace DataEngine.Core.Security;

public sealed partial class TableNameValidator(
    ISchemaCatalogRepository schemaCatalogRepository,
    IMemoryCache cache) : ITableNameValidator
{
    private static readonly Regex IdentifierShape = SafeIdentifierRegex();
    private const string CacheKey = "de:schema-catalog:allowed-tables";

    public async Task EnsureAllowedAsync(string tableName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !IdentifierShape.IsMatch(tableName))
        {
            throw new SqlValidationException($"'{tableName}' is not a valid table identifier.");
        }

        var allowed = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var tables = await schemaCatalogRepository.GetRegisteredTableNamesAsync(ct);
            return new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        });

        if (allowed is null || !allowed.Contains(tableName))
        {
            throw new SqlValidationException($"Table '{tableName}' is not registered for transaction access.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SafeIdentifierRegex();
}
```

Call `await tableNameValidator.EnsureAllowedAsync(tableName, ct)` as the very first line inside `ProcessInsertAsync`, `ProcessUpdateAsync`, and before iterating `delBlock`/`childBlock` keys in `ProcessDeleteOperationsAsync`/`ProcessChildRecordsAsync`, and also treat `mappers.Count == 0` after `GetMappersCachedAsync` as a hard failure rather than proceeding.

---

### P0-2. No idempotency protection on transaction execution

- **File**: `DataEngine.TransactionService/Services/TransactionService.cs`, lines 30–32: `TransactionId` is accepted or generated but only ever used for logging (`_logger.LogInformation(... TxId: {TxId} ...)`), never checked against prior executions.
- **Root cause**: No idempotency store exists in the solution.
- **Impact**: Any client-side or infrastructure-level retry (HTTP timeout + resend, load-balancer retry, mobile app "tap twice") re-executes the full insert/update/delete graph, causing duplicate rows or duplicate side effects with no way to detect it after the fact.
- **Recommendation**: Implement the idempotency design in `IDEMPOTENCY_DESIGN.md` — persist `(TransactionId) → TransactionResult` before committing, and short-circuit on a repeat `TransactionId`.
- **Estimated Effort**: 3–5 days (schema + Redis/DB-backed service + integration into `TransactionAsync`).

### P0-3. `IValidationService` is fully unused — no business-rule or schema validation runs before writes

- **File**: `DataEngine.Core/Interfaces/IValidationService.cs` and `DataEngine.Core/Services/ValidationService.cs` exist; `DataEngine.TransactionService/Services/TransactionService.cs` never references either type, and neither `ServiceCollectionExtensions` file registers `IValidationService`.
- **Root cause**: The validation feature was built against `DataEngine.Core.Domain.ValidationConfiguration`/`BusinessRule` but was never connected to the transaction pipeline.
- **Impact**: Field-level constraints (required, regex, length, business rules) defined in `ValidationConfig.cs` are never enforced. Data of any shape/type reaches the database as long as it satisfies the column's native type at the driver level, and any FluentValidation-style rules configured by an admin are silently ignored, giving a false sense of protection.
- **Recommendation**: Call `IValidationService.ValidateAsync(entityName, data, transaction)` at the top of `TransactionAsync` (and per child record) and return the resulting `ValidationErrors` in `TransactionResult` on failure, before opening/using the DB transaction for writes. Register `IValidationService` in `AddDataEngineTransactionFramework`.
- **Estimated Effort**: 1–2 days (wiring + tests); the service implementation already exists.

### P0-4. Exception messages returned verbatim to HTTP callers

- **File**: `DataEngine.API/Controllers/DataController.cs` line 45 (`StatusCode(500, $"Internal server processing error: {ex.Message}")`); `DataEngine.ReaderService/Services/MySqlGetDataService.cs` line 142 (`CreateFailureResult($"Execution engine failure: {ex.Message}", ...)`); `TransactionService.cs` line 80 (`Message = $"Transaction rolled back: {ex.Message}"`).
- **Root cause**: Raw exception messages (which can include connection string fragments, schema/table names, driver-specific SQL error text) are propagated straight into the HTTP response body.
- **Impact**: Information disclosure that aids further attack (schema enumeration, confirmation of injection attempts, internal hostnames in connection errors).
- **Recommendation**: Log full exception detail server-side (already partially done), but return a generic message + a correlation/trace ID to the caller; keep exception detail out of the response body in non-development environments.
- **Estimated Effort**: 0.5 day.

---

## P1 — High Priority

### P1-1. Oracle read path unimplemented; Oracle write path absent

- **File**: `DataEngine.ReaderService/Services/OracleGetDataService.cs` lines 11–17 — opens a connection, then unconditionally `throw new NotImplementedException(...)`. `TransactionService` has no provider branching at all — it hardcodes MySQL syntax (`` ` `` quoting, `NOW()`, `LAST_INSERT_ID()`).
- **Impact**: Configuring `Provider: Oracle` passes startup verification (which only opens a connection) but fails every subsequent read call, and every write call regardless of configured provider will emit MySQL-dialect SQL against whatever connection was opened.
- **Recommendation**: Either remove Oracle from the supported-provider enum until implemented, or complete it behind the provider-strategy abstraction in `MULTI_DATABASE_DESIGN.md` so both read and write paths are provider-aware.
- **Estimated Effort**: 3–5 days for a functioning Oracle path (SYS_REFCURSOR handling, sequence-based IDs, MERGE-based upsert if desired).

### P1-2. `FetchConfig` filtering/sorting/search fields are accepted but never applied

- **File**: `DataEngine.ReaderService/Domain/FetchConfig.cs` lines 49–79 define `EnableServerSideFiltering`, `FilterConditions`, `EnableServerSideSorting`, `SortField`, `SortDirection`, `SearchText`; `MySqlGetDataService.ExecuteAsync` (lines 39–144) never reads any of them.
- **Impact**: A caller who sets `EnableServerSideFiltering = true` with conditions gets back the same unfiltered page every stored/direct query would return — a silent correctness bug, not a visible error.
- **Recommendation**: Implement a query-builder step that appends parameterized `WHERE`/`ORDER BY` clauses from `FilterConditions`/`SortField` (validating `Column` against the same allow-list as P0-1), or remove the fields from the contract until implemented.
- **Estimated Effort**: 2–3 days.

### P1-3. No row-count verification after UPDATE/DELETE

- **File**: `TransactionService.cs` line 191 (`await command.ExecuteNonQueryAsync(ct); return idValue;`) and line 279 (delete loop) — the affected row count is discarded.
- **Impact**: Updating or deleting a non-existent id returns `Success = true` to the caller, masking a real problem (wrong id, already deleted, race condition).
- **Recommendation**: Capture the `int` return value of `ExecuteNonQueryAsync` and throw/report failure when it is `0` for an update/delete that expected exactly one affected row.
- **Estimated Effort**: 0.5 day.

### P1-4. No optimistic concurrency control

- **File**: `TransactionService.ProcessUpdateAsync`, lines 147–193 — `WHERE \`{idColumn}\` = @targetRecordId` only, no row-version/`updated_at` comparison.
- **Impact**: Two concurrent updates to the same row silently overwrite each other (lost update); the second writer wins with no conflict signal.
- **Recommendation**: Add an optional `RowVersion`/`UpdatedAt` compare-and-swap clause when the field mapper marks a column as a concurrency token; return a specific `Conflict` result when 0 rows match.
- **Estimated Effort**: 1–2 days.

### P1-5. No audit trail for mutations

- **File**: No audit table/service exists anywhere in the solution; `AuditOperation` enum (`DataEngine.Core/Enums/Enums.cs` lines 27–33) is declared but never used.
- **Impact**: No way to answer "who changed this row, from what value, to what value, when" — a baseline requirement for most enterprise compliance regimes.
- **Recommendation**: See `AUDIT_LOGGING_DESIGN.md`.
- **Estimated Effort**: 3–5 days.

---

## P2 — Medium Priority

### P2-1. No caching for metadata reads

- **File**: `FieldMapperRepository.GetFieldMappersAsync` and `QueryRepository.GetQueryDefinitionAsync`/`GetAllQueryDefinitionsAsync` hit the database on every call outside the single-transaction `mappersCache` dictionary in `TransactionService.cs` line 41.
- **Recommendation**: See `CACHING_DESIGN.md` (L1 `IMemoryCache` + L2 Redis, TTL + invalidation).
- **Estimated Effort**: 2–3 days.

### P2-2. No resiliency policies beyond initial connection open

- **File**: `DatabaseConnectionFactory.CreateAndOpenConnectionAsync` (lines 61–111) retries only connection *opening*; no retry/circuit-breaker/timeout wraps the actual `ExecuteReaderAsync`/`ExecuteNonQueryAsync`/`ExecuteScalarAsync` calls in `MySqlGetDataService` or `TransactionService`.
- **Recommendation**: See `RESILIENCY_DESIGN.md` (Polly retry for transient/deadlock errors, circuit breaker, timeout, fallback).
- **Estimated Effort**: 2–3 days.

### P2-3. Blocklist-only SQL firewall

- **File**: `SqlGuardian.cs`, entire file — keyword blocklist (lines 20–28) and regex pattern blocklists (lines 108–116, 132–138, 151–155).
- **Recommendation**: Complement with allow-listing registered query definitions for the "stored query" path (already effectively safe via `QueryRepository`), and treat the direct-query path as inherently higher risk — consider gating it behind a separate permission/role rather than relying solely on pattern matching.
- **Estimated Effort**: 2–4 days depending on desired scope.

### P2-4. Duplicate `DatabaseProvider` enum / Core namespaced as ReaderService

- **File**: `DataEngine.Core/Domain/DatabaseConfig.cs` lines 23–27 vs. `DataEngine.Core/Enums/Enums.cs` lines 36–39; both physically in `DataEngine.Core` but namespaced `DataEngine.ReaderService.Domain` / `DataEngine.ReaderService.Enums`.
- **Recommendation**: Consolidate into one `DataEngine.Core.Enums.DatabaseProvider`; move all Core domain types to `DataEngine.Core.*` namespaces; update references.
- **Estimated Effort**: 1 day (mechanical, but touches many files).

### P2-5. `new Random()` used to fabricate values for unspecified fields

- **File**: `DataEngine.Core/Services/DataTypeConverter.cs`, `GenerateAutoValue` (around lines 313–341 per source listing) — instantiates `new Random()` per call and uses it to synthesize values (including for integer/decimal "auto" columns) when no value or sequence is supplied.
- **Impact**: Not cryptographically strong; more importantly, silently fabricating a plausible-looking value for a missing required field can mask a client bug (missing field is filled with `AUTO_XXXXXXXX` instead of failing validation).
- **Recommendation**: Only auto-generate for fields explicitly marked as such in metadata (e.g., GUID PK generation), and fail validation for any other required field that arrives without a value, rather than fabricating data.
- **Estimated Effort**: 1 day.

---

## P3 — Enhancements

### P3-1. `SELECT SQL_CALC_FOUND_ROWS` is MySQL-specific and known to underperform a plain `COUNT(*)`/window function on large tables

- **File**: `MySqlGetDataService.cs` lines 79–82, 115–117.
- **Recommendation**: Switch to `COUNT(*) OVER()` window column (portable across MySQL 8+, PostgreSQL, SQL Server) inside the provider-strategy abstraction, or a parallel `COUNT(*)` query with the same WHERE clause.
- **Estimated Effort**: 1 day.

### P3-2. No OpenTelemetry / distributed tracing / metrics

- **Recommendation**: See `OBSERVABILITY_DESIGN.md`.
- **Estimated Effort**: 3–5 days.

### P3-3. No API authentication/authorization visible in `Program.cs`

- **Recommendation**: Add an auth scheme (JWT/OAuth2) and `[Authorize]` on both controllers before exposing outside a fully trusted network.
- **Estimated Effort**: 2–3 days (depends on identity provider).
