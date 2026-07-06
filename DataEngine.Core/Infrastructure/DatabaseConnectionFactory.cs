using DataEngine.Core.Providers;
using DataEngine.ReaderService.Domain;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// Multiplexing connection supervisor handling replica routing strategies and recovery loop layers.
/// </summary>
public sealed class DatabaseConnectionFactory
{
    private readonly DatabaseConfig _config;
    private readonly IDbProviderStrategy _strategy;
    private readonly ILogger<DatabaseConnectionFactory> _logger;
    private long _replicaIndex = 0;

    public DatabaseConnectionFactory(DatabaseConfig config, IDbProviderStrategyFactory providerStrategyFactory, ILogger<DatabaseConnectionFactory> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _strategy = providerStrategyFactory?.Get(_config.Provider) ?? throw new ArgumentNullException(nameof(providerStrategyFactory));
    }

    /// <summary>
    /// Allocates an active connection targeting primary master node structures.
    /// </summary>
    public Task<DbConnection> CreatePrimaryConnectionAsync(CancellationToken ct = default)
    {
        if (_config.ConnectionString == null || _config.ConnectionString.Count == 0)
        {
            throw new InvalidOperationException("No connection strings available in configuration.");
        }

        return CreateAndOpenConnectionAsync(_config.ConnectionString[0], "Primary-Master", ct);
    }

    /// <summary>
    /// Yields load balanced read replica nodes employing a thread safe distribution index.
    /// </summary>
    public Task<DbConnection> CreateReadReplicaConnectionAsync(CancellationToken ct = default)
    {
        if (_config.ConnectionString == null || _config.ConnectionString.Count == 0)
        {
            throw new InvalidOperationException("No connection strings available in configuration.");
        }

        if (_config.ConnectionString.Count == 1)
        {
            return CreatePrimaryConnectionAsync(ct);
        }

        int replicaCount = _config.ConnectionString.Count - 1;
        long index = Interlocked.Increment(ref _replicaIndex);
        int selectedIndex = 1 + (int)(index % replicaCount);

        string selectedConnectionString = _config.ConnectionString[selectedIndex];
        return CreateAndOpenConnectionAsync(selectedConnectionString, $"Read-Replica-Node-{selectedIndex}", ct);
    }

    private async Task<DbConnection> CreateAndOpenConnectionAsync(string connectionString, string nodeLabel, CancellationToken ct)
    {
        int attempts = 0;
        int maxAttempts = Math.Max(1, _config.MaxRetryCount);

        while (true)
        {
            attempts++;

            DbConnection connection = _strategy.CreateConnection(connectionString);

            var stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Opening database connection context for target node: {NodeLabel} (Attempt {Attempt}/{Max})", nodeLabel, attempts, maxAttempts);

            try
            {
                await connection.OpenAsync(ct);
                stopwatch.Stop();

                _logger.LogInformation(
                    "Database connection established successfully. Node: {NodeLabel} | Provider: {Provider} | Handshake Latency: {ElapsedMs}ms",
                    nodeLabel, _config.Provider, stopwatch.ElapsedMilliseconds);

                return connection;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await connection.DisposeAsync();

                if (attempts >= maxAttempts || ct.IsCancellationRequested)
                {
                    _logger.LogError(ex,
                        "CRITICAL: Database connection establishment failed permanently. Node: {NodeLabel} | Total Attempts: {Attempts} | Latency: {ElapsedMs}ms | Error: {Message}",
                        nodeLabel, attempts, stopwatch.ElapsedMilliseconds, ex.Message);
                    throw;
                }

                _logger.LogWarning(ex,
                    "Transient connection error encountered on node {NodeLabel}. Backing off for {Delay}ms before retry attempt {NextAttempt}...",
                    nodeLabel, _config.RetryDelayMs, attempts + 1);

                await Task.Delay(_config.RetryDelayMs, ct);
            }
        }
    }
}
