using DataEngine.Core.Configuration;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Threading.Channels;

namespace DataEngine.Core.Services;

/// <summary>
/// Audit logging service. Singleton-safe: uses <see cref="IServiceScopeFactory"/>
/// for scoped database access in the background writer.
/// </summary>
public sealed class AuditService : IAuditService, IAsyncDisposable
{
    private readonly ILogger<AuditService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DataEngineOptions> _options;

    private readonly Channel<ReadAuditRecord> _readAuditChannel;
    private readonly Channel<WriteAuditRecord> _writeAuditChannel;
    private readonly Task _backgroundWriterTask;
    private readonly CancellationTokenSource _shutdownCts = new();

    private record ReadAuditRecord(
        string QueryKey, int RowsRetrieved, string UserId, string Hostname,
        Dictionary<string, object?> QueryParameters, DateTime EnqueuedAtUtc,
        string? ConnectionName);

    private record WriteAuditRecord(
        Guid TransactionId, string TableName, AuditOperation Operation,
        Dictionary<string, object?> Changes, string UserId, string Hostname,
        DateTime EnqueuedAtUtc, string? ConnectionName);

    public AuditService(
        ILogger<AuditService> logger,
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory scopeFactory,
        IOptions<DataEngineOptions> options)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
        _options = options;

        var capacity = Math.Max(100, _options.Value.Audit.ChannelCapacity);

        _readAuditChannel = Channel.CreateBounded<ReadAuditRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _writeAuditChannel = Channel.CreateBounded<WriteAuditRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _backgroundWriterTask = Task.Run(() => BackgroundWriteLoopAsync(_shutdownCts.Token));
    }

    public Task LogAsync(
        Guid transactionId,
        string tableName,
        AuditOperation operation,
        Dictionary<string, object?> changes,
        string userId,
        string? fallbackHost,
        CancellationToken ct,
        string? connectionName = null)
    {
        string hostname = ResolveCurrentHost(fallbackHost);

        _logger.LogInformation(
            "[AUDIT] Tx={TransactionId} Entity={Entity} Op={Operation} User={UserId} Host={Hostname} Changes={@Changes}",
            transactionId, tableName, operation, userId, hostname, changes);

        if (!_options.Value.Audit.PersistWriteAudits)
            return Task.CompletedTask;

        var record = new WriteAuditRecord(
            transactionId, tableName, operation, changes, userId, hostname,
            DateTime.UtcNow, connectionName);

        if (!_writeAuditChannel.Writer.TryWrite(record))
        {
            _logger.LogWarning("Write-audit channel full; record for Tx={TransactionId} was dropped.", transactionId);
        }

        return Task.CompletedTask;
    }

    public Task LogReadAsync(
        string queryKey,
        int rowsRetrieved,
        string userId,
        string? fallbackHost,
        Dictionary<string, object?> queryParameters,
        CancellationToken ct,
        string? connectionName = null)
    {
        string hostname = ResolveCurrentHost(fallbackHost);

        _logger.LogInformation(
            "[AUDIT_READ] QueryKey={QueryKey} Rows={RowsCount} User={UserId} Host={Hostname} Params={@Params}",
            queryKey, rowsRetrieved, userId, hostname, queryParameters);

        if (!_options.Value.Audit.PersistReadAudits)
            return Task.CompletedTask;

        var record = new ReadAuditRecord(
            queryKey ?? "DIRECT_SQL_QUERY", rowsRetrieved, userId ?? "System_Identity",
            hostname, queryParameters ?? [], DateTime.UtcNow, connectionName);

        if (!_readAuditChannel.Writer.TryWrite(record))
        {
            _logger.LogWarning("Read-audit channel full; record for QueryKey={QueryKey} was dropped.", queryKey);
        }

        return Task.CompletedTask;
    }

    private async Task BackgroundWriteLoopAsync(CancellationToken ct)
    {
        var readBatch = new List<ReadAuditRecord>(_options.Value.Audit.BatchSize);
        var writeBatch = new List<WriteAuditRecord>(_options.Value.Audit.BatchSize);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                readBatch.Clear();
                writeBatch.Clear();

                var readWait = _readAuditChannel.Reader.WaitToReadAsync(ct).AsTask();
                var writeWait = _writeAuditChannel.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(readWait, writeWait);

                while (readBatch.Count < _options.Value.Audit.BatchSize
                       && _readAuditChannel.Reader.TryRead(out var readItem))
                {
                    readBatch.Add(readItem);
                }

                while (writeBatch.Count < _options.Value.Audit.BatchSize
                       && _writeAuditChannel.Reader.TryRead(out var writeItem))
                {
                    writeBatch.Add(writeItem);
                }

                if (readBatch.Count > 0)
                    await FlushReadBatchAsync(readBatch, ct);

                if (writeBatch.Count > 0)
                    await FlushWriteBatchAsync(writeBatch, ct);

                if (readBatch.Count == 0 && writeBatch.Count == 0)
                    await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException)
        {
            DrainAndFlush(readBatch, writeBatch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit background writer terminated unexpectedly.");
        }
    }

    private void DrainAndFlush(List<ReadAuditRecord> readBatch, List<WriteAuditRecord> writeBatch)
    {
        while (_readAuditChannel.Reader.TryRead(out var r)) readBatch.Add(r);
        while (_writeAuditChannel.Reader.TryRead(out var w)) writeBatch.Add(w);

        if (readBatch.Count > 0)
            FlushReadBatchAsync(readBatch, CancellationToken.None).GetAwaiter().GetResult();

        if (writeBatch.Count > 0)
            FlushWriteBatchAsync(writeBatch, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task FlushReadBatchAsync(List<ReadAuditRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        foreach (var group in batch.GroupBy(r => r.ConnectionName ?? string.Empty))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<IConnectionContext>();
                var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

                using var _ = string.IsNullOrWhiteSpace(group.Key)
                    ? null
                    : context.UseConnection(group.Key);

                var strategy = factory.GetCurrentStrategy();
                var auditTable = _options.Value.Audit.ReadAuditTableName;

                await using var connection = await factory.CreateConnectionAsync(ct);

                var sql = $"""
                    INSERT INTO {strategy.QuoteIdentifier(auditTable)}
                    (query_key, rows_count, user_id, hostname, query_parameters, created_at)
                    VALUES
                    (@QueryKey, @RowsCount, @UserId, @Hostname, @QueryParameters, {strategy.CurrentTimestampExpression})
                    """;

                var rows = group.Select(r => new
                {
                    QueryKey = r.QueryKey,
                    RowsCount = r.RowsRetrieved,
                    UserId = r.UserId,
                    Hostname = r.Hostname,
                    QueryParameters = r.QueryParameters.Count > 0
                        ? JsonSerializer.Serialize(r.QueryParameters) : null
                });

                await connection.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist {Count} read audit record(s).", group.Count());
            }
        }
    }

    private async Task FlushWriteBatchAsync(List<WriteAuditRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        foreach (var group in batch.GroupBy(r => r.ConnectionName ?? string.Empty))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<IConnectionContext>();
                var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

                using var _ = string.IsNullOrWhiteSpace(group.Key)
                    ? null
                    : context.UseConnection(group.Key);

                var strategy = factory.GetCurrentStrategy();
                var auditTable = _options.Value.Audit.WriteAuditTableName;

                await using var connection = await factory.CreateConnectionAsync(ct);

                var sql = $"""
                    INSERT INTO {strategy.QuoteIdentifier(auditTable)}
                    (transaction_id, table_name, operation, changes_json, user_id, hostname, created_at)
                    VALUES
                    (@TransactionId, @TableName, @Operation, @ChangesJson, @UserId, @Hostname, {strategy.CurrentTimestampExpression})
                    """;

                var rows = group.Select(r => new
                {
                    TransactionId = r.TransactionId.ToString(),
                    TableName = r.TableName,
                    Operation = r.Operation.ToString(),
                    ChangesJson = r.Changes.Count > 0 ? JsonSerializer.Serialize(r.Changes) : null,
                    UserId = r.UserId,
                    Hostname = r.Hostname
                });

                await connection.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist {Count} write audit record(s).", group.Count());
            }
        }
    }

    private string ResolveCurrentHost(string? fallbackHost)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return fallbackHost ?? Environment.MachineName;

        if (context.Request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost)
            && !string.IsNullOrWhiteSpace(forwardedHost))
        {
            return forwardedHost.ToString();
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            if (System.Net.IPAddress.IsLoopback(remoteIp)) return Environment.MachineName;
            return remoteIp.ToString();
        }

        return fallbackHost ?? Environment.MachineName;
    }

    public async ValueTask DisposeAsync()
    {
        _readAuditChannel.Writer.TryComplete();
        _writeAuditChannel.Writer.TryComplete();
        _shutdownCts.Cancel();
        try { await _backgroundWriterTask; } catch { /* logged internally */ }
        _shutdownCts.Dispose();
    }
}

/// <summary>
/// Ensures audit background writer shuts down gracefully with the host.
/// </summary>
public sealed class AuditBackgroundService(IAuditService auditService) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (auditService is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
