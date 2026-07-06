using DataEngine.Core.Providers;
using DataEngine.Core.Resiliency;
using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using DataEngine.ReaderService.Repositories;
using Microsoft.Extensions.Logging;
using Polly;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// Provider-agnostic read service for query execution over configured database targets.
/// </summary>
public sealed class SqlGetDataService : IGetDataService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IQueryRepository _queryRepository;
    private readonly ISqlGuardian _sqlGuardian;
    private readonly DatabaseConfig _config;
    private readonly IDbProviderStrategy _providerStrategy;
    private readonly ILogger<SqlGetDataService> _logger;
    private readonly IAsyncPolicy _policy;

    public SqlGetDataService(
        DatabaseConnectionFactory connectionFactory,
        IQueryRepository queryRepository,
        ISqlGuardian sqlGuardian,
        DatabaseConfig config,
        IDbProviderStrategy providerStrategy,
        ILogger<SqlGetDataService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _queryRepository = queryRepository ?? throw new ArgumentNullException(nameof(queryRepository));
        _sqlGuardian = sqlGuardian ?? throw new ArgumentNullException(nameof(sqlGuardian));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providerStrategy = providerStrategy ?? throw new ArgumentNullException(nameof(providerStrategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policy = ResiliencyPolicyBuilder.BuildPolicy(_providerStrategy, _logger);
    }

    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
    {
        try
        {
            return await _policy.ExecuteAsync(token => ExecuteInternalAsync(query, token), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FATAL DRIVER FAILURE: Error executing fetch pipeline.");
            return CreateFailureResult($"Execution engine failure: {ex.Message}", query, Stopwatch.StartNew().Elapsed);
        }
    }

    private async Task<FetchResult<Dictionary<string, object?>>> ExecuteInternalAsync(FetchConfig query, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string sqlToExecute;

        await using DbConnection connection = await _connectionFactory.CreateReadReplicaConnectionAsync(ct);

        if (query.EnableDirectQueryExecution)
        {
            if (string.IsNullOrWhiteSpace(query.QueryText))
            {
                return CreateFailureResult("Direct query execution enabled, but QueryText is empty.", query, stopwatch.Elapsed);
            }

            _sqlGuardian.ValidateDirectQuery(query.QueryText, _config);
            sqlToExecute = query.QueryText.Trim().TrimEnd(';');
        }
        else
        {
            if (!query.QueryNumber.HasValue && string.IsNullOrWhiteSpace(query.QueryKey))
            {
                return CreateFailureResult("Either QueryNumber or QueryKey must be specified when direct execution is disabled.", query, stopwatch.Elapsed);
            }

            var definition = await _queryRepository.GetQueryDefinitionAsync(query.QueryNumber, query.QueryKey, connection);
            if (definition == null)
            {
                var missingIdentifier = query.QueryNumber?.ToString() ?? query.QueryKey;
                return CreateFailureResult($"Query definition identifier '{missingIdentifier}' not found or is inactive.", query, stopwatch.Elapsed);
            }

            sqlToExecute = definition.QueryText.Trim().TrimEnd(';');
        }

        if (!sqlToExecute.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return CreateFailureResult("Only SELECT queries are supported by the read service.", query, stopwatch.Elapsed);
        }

        // 1. Clean and remove any existing SQL Server pagination clauses if they exist in the definition
        string cleanSql = sqlToExecute;
        if (cleanSql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            int offsetIndex = cleanSql.IndexOf("OFFSET", StringComparison.OrdinalIgnoreCase);
            cleanSql = cleanSql.Substring(0, offsetIndex).Trim();
        }

        int limit = query.Count <= 0 ? 10 : Math.Min(query.Count, _config.MaxPageSize);
        int offset = (Math.Max(1, query.PageNumber) - 1) * limit;

        // 2. Safely parse and normalize custom parameters
        var cachedParams = ExtractJsonParameters(query.InputParameters);

        var paginatedSql = _providerStrategy.BuildPagedQuery(
            cleanSql,
            _providerStrategy.NormalizeParameterName("paginationLimit"),
            _providerStrategy.NormalizeParameterName("paginationOffset")
        );

        await using var command = connection.CreateCommand();
        command.CommandText = paginatedSql;
        ApplyCachedParameters(command, cachedParams);

        var limitParam = command.CreateParameter();
        limitParam.ParameterName = _providerStrategy.NormalizeParameterName("paginationLimit");
        limitParam.Value = limit;
        command.Parameters.Add(limitParam);

        var offsetParam = command.CreateParameter();
        offsetParam.ParameterName = _providerStrategy.NormalizeParameterName("paginationOffset");
        offsetParam.Value = offset;
        command.Parameters.Add(offsetParam);

        var rows = new List<Dictionary<string, object?>>();

        // Fix: Use an isolated block scope so the reader is fully closed and disposed 
        // before the count statement executes on the exact same connection object.
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string name = reader.GetName(i);
                    object value = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                    row[name] = value;
                }
                rows.Add(row);
            }
        } // The reader goes out of scope and is closed here

        // High-performance optimization: Strip column mappings to count rows directly 
        string countSql = cleanSql;
        int fromIndex = countSql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        if (fromIndex >= 0)
        {
            countSql = "SELECT COUNT(*) " + countSql.Substring(fromIndex);
        }
        else
        {
            countSql = $"SELECT COUNT(*) FROM ({cleanSql}) query_count";
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = countSql;
        ApplyCachedParameters(countCommand, cachedParams);

        var totalCount = 0;
        try
        {
            countCommand.CommandTimeout = 60;
            // This will now succeed because the reader is closed!
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Count query timed out or failed. Defaulting total to 0.");
        }

        stopwatch.Stop();

        return new FetchResult<Dictionary<string, object?>>
        {
            Success = true,
            Data = rows,
            TotalCount = totalCount,
            PageNumber = query.PageNumber,
            PageSize = limit,
            ExecutionTime = stopwatch.Elapsed,
            Message = $"Successfully executed query node. Retrieved {rows.Count} rows from a total of {totalCount} matches."
        };
    }

    private static Dictionary<string, object?> ExtractJsonParameters(JsonDocument? inputParameters)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (inputParameters == null) return dictionary;

        foreach (JsonProperty property in inputParameters.RootElement.EnumerateObject())
        {
            // Clean off incoming raw indicators to preserve the root variable key string
            string cleanKey = property.Name.TrimStart('@', ':');

            object? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out long l) ? l : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => property.Value.GetRawText()
            };
            dictionary[cleanKey] = value;
        }

        return dictionary;
    }

    private void ApplyCachedParameters(DbCommand command, Dictionary<string, object?> cachedParams)
    {
        foreach (var param in cachedParams)
        {
            var dbParam = command.CreateParameter();
            // Let the strategy dynamically prepend '@' or ':' based on the provider type
            dbParam.ParameterName = _providerStrategy.NormalizeParameterName(param.Key);
            dbParam.Value = param.Value ?? DBNull.Value;
            command.Parameters.Add(dbParam);
        }
    }

    private static FetchResult<Dictionary<string, object?>> CreateFailureResult(string message, FetchConfig query, TimeSpan elapsed)
    {
        return new FetchResult<Dictionary<string, object?>>
        {
            Success = false,
            Message = message,
            Data = new List<Dictionary<string, object?>>(),
            TotalCount = 0,
            PageNumber = query.PageNumber,
            PageSize = query.Count,
            ExecutionTime = elapsed
        };
    }
}
