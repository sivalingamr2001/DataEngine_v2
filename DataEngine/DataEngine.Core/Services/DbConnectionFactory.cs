using DataEngine.Core.Configuration;
using DataEngine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Data.Common;

namespace DataEngine.Core.Services;

/// <summary>
/// Factory that creates connections with resilience, pooling awareness, and provider routing.
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly IOptions<DataEngineOptions> _options;
    private readonly IDbProviderStrategyFactory _strategyFactory;
    private readonly IConnectionContext _context;
    private readonly ILogger<DbConnectionFactory> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public DbConnectionFactory(
        IOptions<DataEngineOptions> options,
        IDbProviderStrategyFactory strategyFactory,
        IConnectionContext context,
        ILogger<DbConnectionFactory> logger)
    {
        _options = options;
        _strategyFactory = strategyFactory;
        _context = context;
        _logger = logger;

        var defaultOptions = GetDefaultOptions();

        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = defaultOptions.MaxRetryCount,
                Delay = TimeSpan.FromMilliseconds(defaultOptions.RetryDelayMs),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Database connection attempt {Attempt} failed. Retrying...",
                        args.AttemptNumber);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public ValueTask<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var options = ResolveOptions();
        return CreateConnectionInternalAsync(options, cancellationToken);
    }

    public ValueTask<DbConnection> CreateConnectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var options = ResolveOptions(name);
        return CreateConnectionInternalAsync(options, cancellationToken);
    }

    public IDbProviderStrategy GetCurrentStrategy()
    {
        var options = ResolveOptions();
        return _strategyFactory.Get(options.Provider);
    }

    public DatabaseOptions GetCurrentOptions() => ResolveOptions();

    private async ValueTask<DbConnection> CreateConnectionInternalAsync(
        DatabaseOptions options,
        CancellationToken cancellationToken)
    {
        var strategy = _strategyFactory.Get(options.Provider);

        return await _resiliencePipeline.ExecuteAsync(async token =>
        {
            var connection = strategy.CreateConnection(options.ConnectionString);
            await connection.OpenAsync(token);
            _logger.LogDebug("Opened {Provider} connection to {Database}",
                options.Provider, connection.Database);
            return connection;
        }, cancellationToken);
    }

    private DatabaseOptions ResolveOptions(string? name = null)
    {
        var targetName = name ?? _context.TargetConnectionName;

        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var named = _options.Value.Connections.FirstOrDefault(c =>
                c.Name?.Equals(targetName, StringComparison.OrdinalIgnoreCase) == true);

            if (named != null) return named;

            throw new InvalidOperationException(
                $"No database connection configured with name '{targetName}'.");
        }

        return GetDefaultOptions();
    }

    private DatabaseOptions GetDefaultOptions()
    {
        var defaultConn = _options.Value.Connections.FirstOrDefault(c => c.IsDefault)
            ?? _options.Value.Connections.First();

        return defaultConn;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}