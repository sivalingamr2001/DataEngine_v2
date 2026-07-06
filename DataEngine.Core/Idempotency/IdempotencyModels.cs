namespace DataEngine.Core.Idempotency;

public enum IdempotencyStatus
{
    New,
    InProgress,
    Completed,
    Conflict
}

public sealed record IdempotencyClaim(IdempotencyStatus Status, string? CachedResultJson);

public sealed record IdempotencyRecord(
    string TransactionId,
    string EntityName,
    string RequestHash,
    string Status,
    string? ResultJson,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public interface IIdempotencyService
{
    Task<IdempotencyClaim> TryClaimAsync(string transactionId, string entityName, string requestHash, CancellationToken ct);
    Task CompleteAsync(string transactionId, string resultJson, CancellationToken ct);
    Task FailAsync(string transactionId, CancellationToken ct);
}

public interface IIdempotencyRepository
{
    Task InsertInProgressAsync(string transactionId, string entityName, string requestHash, CancellationToken ct);
    Task<IdempotencyRecord?> GetAsync(string transactionId, CancellationToken ct);
    Task MarkCompletedAsync(string transactionId, string resultJson, CancellationToken ct);
    Task MarkFailedAsync(string transactionId, CancellationToken ct);
}
