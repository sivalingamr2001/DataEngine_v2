using Dapper;
using DataEngine.Core.Configuration;
using DataEngine.Core.Domain;
using DataEngine.Core.Enums;
using DataEngine.Core.Exceptions;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Resilience;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text;
using System.Text.Json;

namespace DataEngine.Core.Services;

/// <summary>
/// Modernized high-throughput database-agnostic reader engine.
/// Processes server-side filtering, searching, and optimized sorting execution streams.
/// </summary>
public sealed class ReaderEngine(
    IDbConnectionFactory connectionFactory,
    IQueryRepository queryRepository,
    ISqlGuardian sqlGuardian,
    IAuditService auditService,
    IUserContext userContext,
    ILogger<ReaderEngine> logger,
    TimeProvider? timeProvider = null) : IReaderService, IAsyncDisposable
{
    private readonly IDbConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    private readonly IQueryRepository _queryRepository = queryRepository ?? throw new ArgumentNullException(nameof(queryRepository));
    private readonly ISqlGuardian _sqlGuardian = sqlGuardian ?? throw new ArgumentNullException(nameof(sqlGuardian));
    private readonly IAuditService _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
    private readonly IUserContext _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
    private readonly ILogger<ReaderEngine> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.IsValid)
            throw new ConfigurationException("Invalid validation metadata: Missing query targeting identifier tokens.");

        var startTime = _timeProvider.GetTimestamp();
        var options = _connectionFactory.GetCurrentOptions();
        var strategy = _connectionFactory.GetCurrentStrategy();
        var connectionName = options.Name;
        var auditUser = _userContext.UserId ?? "Anonymous";

        try
        {
            var (rows, totalCount) = await TransientRetryExecutor.ExecuteAsync(
                async () =>
                {
                    await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
                    return await ExecuteQueryCoreAsync(query, connection, options, strategy, ct);
                },
                strategy, _logger, maxAttempts: 3, ct);

            var elapsed = _timeProvider.GetElapsedTime(startTime);

            _ = _auditService.LogReadAsync(
                query.QueryKey ?? "DIRECT_SQL_QUERY",
                rows.Count,
                auditUser,
                null,
                query.Parameters,
                ct,
                connectionName);

            return new FetchResult<Dictionary<string, object?>>
            {
                Success = true,
                Data = rows!,
                TotalCount = totalCount,
                PageNumber = Math.Max(1, query.PageNumber),
                PageSize = Math.Clamp(query.Count, 1, options.MaxPageSize),
                ExecutionTime = elapsed,
                Message = $"Successfully executed query node. Retrieved {rows.Count} rows from a total of {totalCount} matches."
            };
        }
        catch (SqlValidationException ex)
        {
            _logger.LogWarning(ex, "SQL Guardian rejected incoming pipeline query payload execution context.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal driver tracking failure processing payload execution pipeline.");

            // CHANGED: safe message, preserving root cause only in logs.
            throw new QueryExecutionException(SqlErrorTranslator.ToSafeMessage(ex), ex);
        }
    }

    private async Task<(List<Dictionary<string, object?>> Rows, int TotalCount)> ExecuteQueryCoreAsync(
        FetchConfig query, IDbConnection connection, DatabaseOptions options, IDbProviderStrategy strategy, CancellationToken ct)
    {
        // 1. Resolve Underlying Base SQL String safely
        var (sqlToExecute, isDirect) = await ResolveQueryAsync(query, connection, options, ct);

        // 2. Security Firewall Pre-Validation Checks
        if (isDirect)
            _sqlGuardian.ValidateDirectQuery(sqlToExecute, options);
        else
        {
            _sqlGuardian.ValidateReadOnlyQuery(sqlToExecute);
            _sqlGuardian.ValidateQueryComplexity(sqlToExecute, options);
        }

        var parameters = new DynamicParameters();
        var (filteredAndSortedSql, filterOnlySql) = BuildFilteredSortedQuery(sqlToExecute, query, options, strategy, parameters);

        var commandTimeout = options.CommandTimeoutSeconds > 0 ? options.CommandTimeoutSeconds : (int?)null;

        int totalCount;
        if (query.IncludeTotalCount)
        {
            var countSql = $"SELECT COUNT(*) FROM ({filterOnlySql}) AS count_target_subquery";
            totalCount = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(countSql, parameters, commandTimeout: commandTimeout, cancellationToken: ct));
        }
        else
        {
            totalCount = -1;
        }

        // 6. Pagination Clamp Computations
        var pageSize = Math.Clamp(query.Count, 1, options.MaxPageSize);
        var pageNumber = Math.Max(1, query.PageNumber);
        var offset = (pageNumber - 1) * pageSize;

        // 7. Inject Infrastructure Pagination Engine parameters
        parameters.Add(strategy.NormalizeParameterName("pageSize"), pageSize);
        parameters.Add(strategy.NormalizeParameterName("offset"), offset);
        var pagedSql = strategy.BuildPagedQuery(filteredAndSortedSql, "pageSize", "offset");

        // 8. Dynamic Target Row Data Retrieval and Projection
        var rawResult = await connection.QueryAsync<dynamic>(
            new CommandDefinition(pagedSql, parameters, commandTimeout: commandTimeout, cancellationToken: ct));

        var rows = rawResult
            .Select(row => ((IDictionary<string, object>)row).ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();

        return (rows, totalCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ARCHITECTURAL PRIVATE PIPELINE HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private async Task<(string Sql, bool IsDirect)> ResolveQueryAsync(
        FetchConfig query, IDbConnection connection, DatabaseOptions options, CancellationToken ct)
    {
        if (query.EnableDirectQueryExecution && !string.IsNullOrWhiteSpace(query.QueryText))
        {
            if (!options.EnableDirectQueryExecution)
                throw new SqlValidationException("Direct query execution is strictly disabled globally.");

            return (query.QueryText.Trim().TrimEnd(';'), true);
        }

        if (!query.QueryNumber.HasValue && string.IsNullOrWhiteSpace(query.QueryKey))
        {
            throw new ConfigurationException("Invalid validation metadata: Missing query targeting identifier tokens.");
        }

        var definition = await _queryRepository.GetQueryDefinitionAsync(query.QueryNumber, query.QueryKey, connection, ct)
            ?? throw new QueryExecutionException("No query definition metadata is available for the supplied identifier.");

        return (definition.QueryText.Trim().TrimEnd(';'), false);
    }

    /// <summary>
    /// Builds filter + sort in a single derived-table wrap.
    /// Returns (fullSqlWithFilterAndSort, filterOnlySqlForCounting).
    /// </summary>
    private (string FullSql, string FilterOnlySql) BuildFilteredSortedQuery(
        string baseSql, FetchConfig query, DatabaseOptions options, IDbProviderStrategy strategy, DynamicParameters dapperParams)
    {
        if (query.Parameters != null)
        {
            foreach (var kv in query.Parameters)
            {
                dapperParams.Add(strategy.NormalizeParameterName(kv.Key), NormalizeParameterValue(kv.Value));
            }
        }

        var filterBuilder = new StringBuilder();
        int indexCounter = 0;

        if (query.EnableServerSideFiltering && query.FilterConditions != null)
        {
            foreach (var condition in query.FilterConditions)
            {
                _sqlGuardian.ValidateFieldName(condition.Column);

                string safeColumn = strategy.QuoteIdentifier(condition.Column);
                string virtualParamKey = $"dynFilterParam_{indexCounter++}";
                string fullParamPlaceholder = $"{strategy.ParameterPrefix}{virtualParamKey}";

                string sqlOp = condition.Operator switch
                {
                    FilterOperator.Equals => "=",
                    FilterOperator.NotEquals => "<>",
                    FilterOperator.GreaterThan => ">",
                    FilterOperator.GreaterThanOrEqual => ">=",
                    FilterOperator.LessThan => "<",
                    FilterOperator.LessThanOrEqual => "<=",
                    FilterOperator.Contains => "LIKE",
                    FilterOperator.StartsWith => "LIKE",
                    FilterOperator.EndsWith => "LIKE",
                    FilterOperator.In => "IN",       // FIXED: now actually works, see below
                    FilterOperator.IsNull => "IS NULL",
                    FilterOperator.IsNotNull => "IS NOT NULL",
                    _ => throw new QueryExecutionException($"Unsupported operator type: {condition.Operator}")
                };

                if (condition.Operator == FilterOperator.IsNull || condition.Operator == FilterOperator.IsNotNull)
                {
                    filterBuilder.Append($" AND {safeColumn} {sqlOp}");
                }
                else
                {
                    // Dapper auto-expands "IN @param" into "IN (@param1,@param2,...)"
                    // when the bound value is an IEnumerable — no manual parens needed.
                    filterBuilder.Append($" AND {safeColumn} {sqlOp} {fullParamPlaceholder}");
                    object? extractedValue = ExtractJsonElementValue(condition.Value);

                    if (condition.Operator == FilterOperator.In)
                    {
                        // FIXED: guard against a non-array value being sent for "In".
                        if (extractedValue is not IEnumerable<object?> list)
                            throw new SqlValidationException($"Filter operator 'In' on column '{condition.Column}' requires an array value.");

                        dapperParams.Add(virtualParamKey, list.ToList());
                        continue;
                    }

                    if (extractedValue is string stringValue)
                    {
                        extractedValue = condition.Operator switch
                        {
                            FilterOperator.Contains => $"%{EscapeLikePattern(stringValue)}%",
                            FilterOperator.StartsWith => $"{EscapeLikePattern(stringValue)}%",
                            FilterOperator.EndsWith => $"%{EscapeLikePattern(stringValue)}",
                            _ => stringValue
                        };
                    }

                    dapperParams.Add(virtualParamKey, extractedValue);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var searchColumn = ResolveSearchColumn(query, options);
            if (!string.IsNullOrWhiteSpace(searchColumn))
            {
                string cleanSearchColumn = strategy.QuoteIdentifier(searchColumn);
                filterBuilder.Append($" AND {cleanSearchColumn} LIKE {strategy.ParameterPrefix}globalSearchValue");
                dapperParams.Add("globalSearchValue", $"%{EscapeLikePattern(query.SearchText)}%");
            }
        }

        var filterOnlySql = filterBuilder.Length > 0
            ? $"SELECT * FROM ({baseSql}) AS query_filter_wrapper WHERE 1=1{filterBuilder}"
            : baseSql;

        // Sorting is applied on top of the SAME wrapper reference (single nesting level)
        // rather than re-wrapping filterOnlySql a second time.
        if (!query.EnableServerSideSorting || string.IsNullOrWhiteSpace(query.SortField))
        {
            return (filterOnlySql, filterOnlySql);
        }

        _sqlGuardian.ValidateSortField(query.SortField);
        string safeSortField = strategy.QuoteIdentifier(query.SortField);
        string sortDirectionKeyword = query.SortDirection == SortDirection.Asc ? "ASC" : "DESC";

        var fullSql = filterBuilder.Length > 0
            ? $"{filterOnlySql} ORDER BY {safeSortField} {sortDirectionKeyword}"
            : $"SELECT * FROM ({baseSql}) AS sorted_target_wrapper ORDER BY {safeSortField} {sortDirectionKeyword}";

        return (fullSql, filterOnlySql);
    }

    private static string? ResolveSearchColumn(FetchConfig query, DatabaseOptions options)
    {
        if (!string.IsNullOrWhiteSpace(query.SearchColumn))
            return query.SearchColumn;

        if (!string.IsNullOrWhiteSpace(query.QueryKey)
            && options.SearchColumnOverrides.TryGetValue(query.QueryKey, out var overrideCol))
        {
            return overrideCol;
        }

        return options.DefaultSearchColumn;
    }

    private static string EscapeLikePattern(string input) =>
        input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static object? ExtractJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            // FIXED: arrays are now converted to a real list instead of falling
            // through to GetRawText() (which produced a single bogus string like "[1,2,3]").
            JsonValueKind.Array => element.EnumerateArray().Select(ExtractJsonElementValue).ToList(),
            _ => element.GetRawText()
        };
    }

    private static object? NormalizeParameterValue(object? value)
    {
        return value switch
        {
            JsonElement element => ExtractJsonElementValue(element),
            _ => value
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
