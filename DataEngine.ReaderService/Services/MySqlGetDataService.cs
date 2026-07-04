using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// High throughput reading provider executing query payloads over MySQL database clusters.
/// </summary>
public sealed class MySqlGetDataService : IGetDataService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IQueryRepository _queryRepository;
    private readonly ISqlGuardian _sqlGuardian;
    private readonly DatabaseConfig _config;
    private readonly ILogger<MySqlGetDataService> _logger;

    public MySqlGetDataService(
        DatabaseConnectionFactory connectionFactory,
        IQueryRepository queryRepository,
        ISqlGuardian sqlGuardian,
        DatabaseConfig config,
        ILogger<MySqlGetDataService> _logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _queryRepository = queryRepository ?? throw new ArgumentNullException(nameof(queryRepository));
        _sqlGuardian = sqlGuardian ?? throw new ArgumentNullException(nameof(sqlGuardian));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        this._logger = _logger ?? throw new ArgumentNullException(nameof(_logger));
    }

    /// <inheritdoc />
    /// <summary>
    /// Executes validated data extraction queries asynchronously returning enveloped paginated datasets.
    /// </summary>
    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string sqlToExecute = string.Empty;

        try
        {
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

            int limit = query.Count <= 0 ? 10 : query.Count;
            int offset = (Math.Max(1, query.PageNumber) - 1) * limit;

            var cachedParams = ExtractJsonParameters(query.InputParameters);

            if (sqlToExecute.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                sqlToExecute = "SELECT SQL_CALC_FOUND_ROWS " + sqlToExecute.Substring(6);
            }

            string paginatedSql = $"{sqlToExecute} LIMIT @paginationLimit OFFSET @paginationOffset";
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = paginatedSql;
            ApplyCachedParameters(command, cachedParams);

            DbParameter limitParam = command.CreateParameter();
            limitParam.ParameterName = "@paginationLimit";
            limitParam.Value = limit;
            command.Parameters.Add(limitParam);

            DbParameter offsetParam = command.CreateParameter();
            offsetParam.ParameterName = "@paginationOffset";
            offsetParam.Value = offset;
            command.Parameters.Add(offsetParam);

            var rows = new List<Dictionary<string, object?>>();
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

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
            await reader.CloseAsync();

            await using DbCommand countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT FOUND_ROWS()";
            int totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));

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
        catch (SqlValidationException valEx)
        {
            stopwatch.Stop();
            _logger.LogWarning(valEx, "SQL Firewall rejected request execution path.");
            return CreateFailureResult($"Security Boundary Violation: {valEx.Message}", query, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "FATAL DRIVER FAILURE: Error executing MySQL fetch pipeline.");
            return CreateFailureResult($"Execution engine failure: {ex.Message}", query, stopwatch.Elapsed);
        }
    }

    private static Dictionary<string, object?> ExtractJsonParameters(JsonDocument? inputParameters)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (inputParameters == null) return dictionary;

        foreach (JsonProperty property in inputParameters.RootElement.EnumerateObject())
        {
            string name = property.Name.StartsWith("@") ? property.Name : $"@{property.Name}";
            object? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out long l) ? l : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => property.Value.GetRawText()
            };
            dictionary[name] = value;
        }

        return dictionary;
    }

    private static void ApplyCachedParameters(DbCommand command, Dictionary<string, object?> cachedParams)
    {
        foreach (var param in cachedParams)
        {
            DbParameter dbParam = command.CreateParameter();
            dbParam.ParameterName = param.Key;
            dbParam.Value = param.Value;
            command.Parameters.Add(dbParam);
        }
    }

    private static FetchResult<Dictionary<string, object?>> CreateFailureResult(string message, FetchConfig query, TimeSpan elapsed)
    {
        return new FetchResult<Dictionary<string, object?>>
        {
            Success = false,
            Message = message,
            PageNumber = query.PageNumber,
            PageSize = query.Count,
            ExecutionTime = elapsed
        };
    }
}
