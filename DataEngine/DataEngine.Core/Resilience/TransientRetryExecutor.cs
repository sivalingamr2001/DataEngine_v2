using DataEngine.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DataEngine.Core.Resilience;

/// <summary>
/// Lightweight transient-fault retry executor. Retries only when
/// IDbProviderStrategy.IsTransient(ex) returns true (deadlocks, lock
/// timeouts, transient connection faults) — never for validation or
/// business-logic exceptions.
/// </summary>
public static class TransientRetryExecutor
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        IDbProviderStrategy strategy,
        ILogger logger,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        var attempt = 0;
        var delay = TimeSpan.FromMilliseconds(100);

        while (true)
        {
            attempt++;
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && strategy.IsTransient(ex))
            {
                logger.LogWarning(ex,
                    "Transient database fault on attempt {Attempt}/{MaxAttempts}. Retrying in {DelayMs}ms.",
                    attempt, maxAttempts, delay.TotalMilliseconds);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2; // exponential backoff
            }
        }
    }

    public static async Task ExecuteAsync(
        Func<Task> operation,
        IDbProviderStrategy strategy,
        ILogger logger,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        await ExecuteAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, strategy, logger, maxAttempts, ct).ConfigureAwait(false);
    }
}
