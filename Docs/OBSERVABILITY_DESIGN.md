# OBSERVABILITY_DESIGN.md

## Current State

Serilog is configured in `DataEngine.API/Program.cs` (console sink, `UseSerilogRequestLogging`, structured properties like `{NodeLabel}`/`{Attempt}` inside `DatabaseConnectionFactory`/`DatabaseConnectionVerifier` logging calls). This is a reasonable logging foundation but there is no tracing, no metrics, and no propagated correlation ID connecting an HTTP request to the SQL it triggered to the audit row it produced.

## OpenTelemetry Wiring

```csharp
// Program.cs additions
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("DataEngine.API"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("DataEngine.TransactionService")
        .AddSource("DataEngine.ReaderService")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter("DataEngine.Metrics")
        .AddOtlpExporter());
```

## Correlation ID Propagation

```csharp
// Middleware, registered before UseSerilogRequestLogging
app.Use(async (context, next) =>
{
    string correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var existing)
        ? existing.ToString()
        : Guid.NewGuid().ToString();

    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;

    using (LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});
```

Pass `context.Items["CorrelationId"]` into `TransactionRequest`/audit calls at the controller boundary, so the same ID appears in: the HTTP response header, every Serilog line for that request, the `de_audit_log.correlation_id` column, and the OpenTelemetry trace/span attributes.

## Custom Spans Around Data Operations

```csharp
private static readonly ActivitySource TxActivitySource = new("DataEngine.TransactionService");

public async Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken ct = default)
{
    using var activity = TxActivitySource.StartActivity("TransactionService.Execute", ActivityKind.Internal);
    activity?.SetTag("de.entity", request.TransactionEntityName);
    activity?.SetTag("de.transaction_id", request.TransactionId);
    // ... existing logic ...
}
```

## Metrics to Track

```csharp
public static class DataEngineMetrics
{
    public static readonly Meter Meter = new("DataEngine.Metrics");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("de.request.duration_ms");

    public static readonly Histogram<double> TransactionDuration =
        Meter.CreateHistogram<double>("de.transaction.duration_ms");

    public static readonly Counter<long> Rollbacks =
        Meter.CreateCounter<long>("de.transaction.rollbacks");

    public static readonly Counter<long> FailureCount =
        Meter.CreateCounter<long>("de.request.failures");

    public static readonly Counter<long> DatabaseCalls =
        Meter.CreateCounter<long>("de.database.calls");

    public static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("de.cache.hits");

    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("de.cache.misses");
}
```

Emit `Rollbacks.Add(1, new("entity", request.TransactionEntityName))` in the `catch` block of `TransactionAsync` (currently only logs via `_logger.LogError`), and `TransactionDuration.Record(stopwatch.Elapsed.TotalMilliseconds)` around the whole method — the pattern already exists for `MySqlGetDataService` (it has a `Stopwatch` today, lines 41/119/134/140, just not exported as a metric).

## Structured Logging Enhancements

- Standardize a small set of Serilog enrichers so every log line automatically carries `CorrelationId`, `TransactionId` (when applicable), and `UserId` without every call site needing to remember to pass them — use `LogContext.PushProperty` at the controller boundary, as shown above.
- Replace the ad hoc, prose-heavy log messages currently in `DatabaseConnectionFactory`/`TransactionService` (e.g. "FATAL TRANSMISSION CRASH: Rolling back execution scope") with consistent, greppable event names (e.g. `EventId` per operation) so dashboards/alerts can key off event IDs rather than message text.

## Dashboards / Alerting (recommendation, not code)

- Request duration p50/p95/p99 per endpoint.
- Transaction rollback rate (rollbacks / total transactions) — alert if it exceeds a baseline threshold, since a spike usually indicates either a bad deploy or an attack attempt (e.g., many rejected `SqlValidationException`s from P0-1's fix).
- Cache hit ratio for metadata lookups — a sudden drop indicates cache invalidation storms or a cold restart wave.
- Database call count per request — should stay flat/low after the caching and query-builder work; a regression here is an early performance-regression signal.
