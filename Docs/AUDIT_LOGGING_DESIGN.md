# AUDIT_LOGGING_DESIGN.md — Enterprise Auditing

## Problem

`DataEngine.Core/Enums/Enums.cs` declares an `AuditOperation` enum (`Create`, `Update`, `Delete`) but nothing in the solution ever constructs, persists, or reads an audit record. There is currently no way to answer "who changed this row, what did it look like before, what does it look like now, when, and from where."

## Schema Design

```sql
CREATE TABLE de_audit_log (
    audit_id        BIGINT AUTO_INCREMENT PRIMARY KEY,
    transaction_id  VARCHAR(100)  NOT NULL,
    entity          VARCHAR(128)  NOT NULL,
    record_id       VARCHAR(100)  NOT NULL,
    operation       VARCHAR(10)   NOT NULL,     -- Create | Update | Delete
    before_data     JSON          NULL,
    after_data      JSON          NULL,
    user_id         VARCHAR(100)  NOT NULL,
    correlation_id  VARCHAR(100)  NOT NULL,
    ip_address      VARCHAR(45)   NULL,          -- IPv4/IPv6
    created_at      DATETIME(6)   NOT NULL,

    INDEX ix_de_audit_entity_record (entity, record_id),
    INDEX ix_de_audit_transaction (transaction_id),
    INDEX ix_de_audit_created_at (created_at)
);
```

`before_data`/`after_data` store the full row image as JSON so the audit record is self-describing without needing to join back to the (possibly since-changed) source schema.

## Capture Points

Audit records must be written **inside the same database transaction** as the mutation they describe, so a rollback also rolls back the audit entry — an audit log that can diverge from reality is worse than none.

```csharp
namespace DataEngine.Core.Auditing;

public interface IAuditService
{
    Task RecordAsync(AuditEntry entry, DbConnection connection, DbTransaction transaction, CancellationToken ct);
}

public sealed record AuditEntry(
    string TransactionId,
    string Entity,
    string RecordId,
    AuditOperation Operation,
    object? BeforeData,
    object? AfterData,
    string UserId,
    string CorrelationId,
    string? IpAddress);

public sealed class AuditService : IAuditService
{
    public async Task RecordAsync(AuditEntry entry, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO de_audit_log
                (transaction_id, entity, record_id, operation, before_data, after_data, user_id, correlation_id, ip_address, created_at)
            VALUES
                (@txId, @entity, @recordId, @op, @before, @after, @userId, @correlationId, @ip, UTC_TIMESTAMP(6))";

        AddParam(cmd, "@txId", entry.TransactionId);
        AddParam(cmd, "@entity", entry.Entity);
        AddParam(cmd, "@recordId", entry.RecordId);
        AddParam(cmd, "@op", entry.Operation.ToString());
        AddParam(cmd, "@before", entry.BeforeData is null ? DBNull.Value : JsonSerializer.Serialize(entry.BeforeData));
        AddParam(cmd, "@after", entry.AfterData is null ? DBNull.Value : JsonSerializer.Serialize(entry.AfterData));
        AddParam(cmd, "@userId", entry.UserId);
        AddParam(cmd, "@correlationId", entry.CorrelationId);
        AddParam(cmd, "@ip", (object?)entry.IpAddress ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
```

## Integration Points in `TransactionService`

- **Insert** (`ProcessInsertAsync`): after `ExecuteScalarAsync` returns the new id, call `RecordAsync(new AuditEntry(txId, tableName, newId.ToString(), AuditOperation.Create, BeforeData: null, AfterData: data, userId, correlationId, ip), conn, tx, ct)`.
- **Update** (`ProcessUpdateAsync`): **read the current row inside the same transaction before applying the UPDATE** (a `SELECT ... WHERE id = @id FOR UPDATE`/equivalent row lock, which also happens to be the natural place to add the optimistic-concurrency check from `CRITICAL_ISSUES.md` P1-4), pass that as `BeforeData`, and the post-update field values as `AfterData`.
- **Delete** (`ProcessDeleteOperationsAsync`): read the row before deleting it, pass it as `BeforeData`, `AfterData: null`.
- `UserId` comes from `TransactionRequest.UserId` (already present); `CorrelationId` should come from the HTTP request's trace/correlation header (see `OBSERVABILITY_DESIGN.md`) rather than being invented per-call; `IpAddress` from `HttpContext.Connection.RemoteIpAddress`, passed down from the controller into the request rather than resolved inside the service (keeps `TransactionService` free of `HttpContext` dependencies).

## Why Inside the Transaction, Not a Separate Async Pipeline

A queued/eventual audit pipeline (e.g., fire-and-forget to a message bus) is tempting for performance, but it reintroduces exactly the consistency gap this design exists to close — a crash between commit and publish would silently lose an audit record for a mutation that did happen. Writing the audit row in the same DB transaction guarantees the audit trail and the data are always consistent; if audit-write latency becomes a measurable bottleneck at very high volume, revisit with a durable outbox pattern (write audit intent transactionally, publish asynchronously from the outbox) rather than dropping the transactional guarantee outright.
