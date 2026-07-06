using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace DataEngine.Core.Idempotency;

public sealed class IdempotencyService : IIdempotencyService
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromDays(7);
    private readonly IConnectionMultiplexer _redis;
    private readonly IIdempotencyRepository _dbFallback;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(IConnectionMultiplexer redis, IIdempotencyRepository dbFallback, ILogger<IdempotencyService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _dbFallback = dbFallback ?? throw new ArgumentNullException(nameof(dbFallback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IdempotencyClaim> TryClaimAsync(string transactionId, string entityName, string requestHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) throw new ArgumentNullException(nameof(transactionId));
        if (string.IsNullOrWhiteSpace(entityName)) throw new ArgumentNullException(nameof(entityName));
        if (string.IsNullOrWhiteSpace(requestHash)) throw new ArgumentNullException(nameof(requestHash));

        var db = _redis.GetDatabase();
        string key = $"de:idem:{transactionId}";

        try
        {
            bool claimed = await db.StringSetAsync(key, requestHash, LockTtl, When.NotExists);
            if (claimed)
            {
                await _dbFallback.InsertInProgressAsync(transactionId, entityName, requestHash, ct);
                return new IdempotencyClaim(IdempotencyStatus.New, null);
            }

            var existingHash = await db.StringGetAsync(key);
            if (existingHash.HasValue && existingHash != requestHash)
            {
                return new IdempotencyClaim(IdempotencyStatus.Conflict, null);
            }

            var record = await _dbFallback.GetAsync(transactionId, ct);
            if (record is null)
            {
                return new IdempotencyClaim(IdempotencyStatus.InProgress, null);
            }

            if (record.RequestHash != requestHash)
            {
                return new IdempotencyClaim(IdempotencyStatus.Conflict, null);
            }

            return record.Status switch
            {
                "Completed" => new IdempotencyClaim(IdempotencyStatus.Completed, record.ResultJson),
                "Failed" => new IdempotencyClaim(IdempotencyStatus.New, null),
                _ => new IdempotencyClaim(IdempotencyStatus.InProgress, null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency claim lookup failed for transaction {TransactionId}. Falling back to database state.", transactionId);
            var record = await _dbFallback.GetAsync(transactionId, ct);
            if (record is null)
            {
                return new IdempotencyClaim(IdempotencyStatus.New, null);
            }

            if (record.RequestHash != requestHash)
            {
                return new IdempotencyClaim(IdempotencyStatus.Conflict, null);
            }

            return record.Status switch
            {
                "Completed" => new IdempotencyClaim(IdempotencyStatus.Completed, record.ResultJson),
                "Failed" => new IdempotencyClaim(IdempotencyStatus.New, null),
                _ => new IdempotencyClaim(IdempotencyStatus.InProgress, null)
            };
        }
    }

    public async Task CompleteAsync(string transactionId, string resultJson, CancellationToken ct)
    {
        await _dbFallback.MarkCompletedAsync(transactionId, resultJson, ct);
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"de:idem:{transactionId}:result", resultJson, ResultTtl);
        await db.KeyDeleteAsync($"de:idem:{transactionId}");
    }

    public async Task FailAsync(string transactionId, CancellationToken ct)
    {
        await _dbFallback.MarkFailedAsync(transactionId, ct);
        await _redis.GetDatabase().KeyDeleteAsync($"de:idem:{transactionId}");
    }
}
