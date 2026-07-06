using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace DataEngine.Core.Resiliency;

public static class ResiliencyPolicyBuilder
{
    // Added optional parameter to control timeout configuration dynamically
    public static IAsyncPolicy BuildPolicy(
        DataEngine.Core.Providers.IDbProviderStrategy strategy,
        ILogger logger,
        int timeoutInSeconds = 60)
    {
        if (strategy == null) throw new ArgumentNullException(nameof(strategy));
        if (logger == null) throw new ArgumentNullException(nameof(logger));

        Func<Exception, bool> isTransient = ex => strategy.IsTransient(ex);

        var retry = Policy.Handle<Exception>(isTransient)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(150 * attempt + Random.Shared.Next(0, 100)),
                onRetry: (exception, timespan, retryCount, context) =>
                {
                    logger.LogWarning(exception, "Transient database error detected. Retrying attempt {RetryCount} after {Delay}ms.", retryCount, timespan.TotalMilliseconds);
                });

        var circuitBreaker = Policy.Handle<Exception>(isTransient)
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 2,
                durationOfBreak: TimeSpan.FromSeconds(20),
                onBreak: (exception, duration) =>
                {
                    logger.LogWarning(exception, "Circuit breaker opened for {Duration} due to repeated transient database failures.", duration.TotalSeconds);
                },
                onReset: () =>
                {
                    logger.LogInformation("Circuit breaker reset to closed state.");
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("Circuit breaker is half-open and will test the next call.");
                });

        // Changed to Optimistic timeout since the downstream database service natively supports CancellationTokens
        var timeout = Policy.TimeoutAsync(TimeSpan.FromSeconds(timeoutInSeconds), TimeoutStrategy.Optimistic,
            onTimeoutAsync: (context, timespan, task, ct) =>
            {
                logger.LogWarning("Database operation timed out after {TimeoutMs}ms.", timespan.TotalMilliseconds);
                return Task.CompletedTask;
            });

        return Policy.WrapAsync(retry, circuitBreaker, timeout);
    }
}
