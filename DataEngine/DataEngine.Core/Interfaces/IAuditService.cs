using DataEngine.Core.Enums;

namespace DataEngine.Core.Interfaces;

/// <summary>Audit logging service.</summary>
public interface IAuditService
{
    Task LogAsync(
        Guid transactionId,
        string tableName,
        AuditOperation operation,
        Dictionary<string, object?> changes,
        string userId,
        string? hostname,
        CancellationToken ct,
        string? connectionName = null);

    Task LogReadAsync(
        string queryKey,
        int rowsRetrieved,
        string userId,
        string? hostname,
        Dictionary<string, object?> queryParameters,
        CancellationToken ct,
        string? connectionName = null);
}