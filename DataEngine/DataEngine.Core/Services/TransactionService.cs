using Dapper;
using DataEngine.Core.Configuration;
using DataEngine.Core.Domain;
using DataEngine.Core.Enums;
using DataEngine.Core.Exceptions;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Resilience;
using DataEngine.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Text.Json;

namespace DataEngine.Core.Services;

/// <summary>
/// Production-ready transaction engine supporting multi-provider databases.
/// Uses IDbProviderStrategy for dialect-specific SQL generation.
/// </summary>
public sealed class TransactionService : ITransactionService, IAsyncDisposable
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IFieldMapperRepository _fieldMapperRepository;
    private readonly IAuditService _auditService;
    private readonly IValidationService _validationService;
    private readonly ITableNameValidator _tableNameValidator;
    private readonly ISqlGuardian _sqlGuardian;
    private readonly IMemoryCache _mapperCache;
    private readonly ILogger<TransactionService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IUserContext _userContext;
    private readonly AuditColumnOptions _auditColumns;

    private const int MaxRecursionDepth = 5;
    private static readonly TimeSpan MapperCacheTtl = TimeSpan.FromMinutes(10);

    public TransactionService(
        IDbConnectionFactory connectionFactory,
        IFieldMapperRepository fieldMapperRepository,
        IAuditService auditService,
        IValidationService validationService,
        ITableNameValidator tableNameValidator,
        ISqlGuardian sqlGuardian,
        IMemoryCache mapperCache,
        ILogger<TransactionService> logger,
        IUserContext userContext,
        IOptions<DataEngineOptions> engineOptions,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _fieldMapperRepository = fieldMapperRepository ?? throw new ArgumentNullException(nameof(fieldMapperRepository));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _tableNameValidator = tableNameValidator ?? throw new ArgumentNullException(nameof(tableNameValidator));
        _sqlGuardian = sqlGuardian ?? throw new ArgumentNullException(nameof(sqlGuardian));
        _mapperCache = mapperCache ?? throw new ArgumentNullException(nameof(mapperCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        _auditColumns = engineOptions.Value.Audit.AuditColumns;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TransactionEntityName))
            return Fail("TransactionEntityName is required.", Guid.Empty);

        Guid transactionId = request.TransactionId == Guid.Empty
            ? Guid.NewGuid()
            : request.TransactionId;

        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? request.EffectiveTransactionId
            : request.CorrelationId;

        var userId = _userContext.UserId
            ?? (string.IsNullOrWhiteSpace(request.UserId) ? "System" : request.UserId);

        var connectionName = _connectionFactory.GetCurrentOptions().Name;
        var strategy = _connectionFactory.GetCurrentStrategy();

        _logger.LogInformation("Tx:{TxId} Starting transaction on {Table}", transactionId, request.TransactionEntityName);

        try
        {
            return await TransientRetryExecutor.ExecuteAsync(async () =>
            {
                await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted, cancellationToken);

                try
                {
                    await _tableNameValidator.EnsureAllowedAsync(request.TransactionEntityName, cancellationToken);

                    if (request.ExtendedProperties.Count > 0)
                    {
                        var validation = await _validationService.ValidateAsync(
                            request.TransactionEntityName, request.ExtendedProperties, transaction, cancellationToken);

                        if (!validation.IsValid)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            return new TransactionResult
                            {
                                Success = false,
                                TransactionId = transactionId,
                                Message = "Validation failed.",
                                ValidationErrors = validation.Errors.ToList()
                            };
                        }
                    }

                    var mainMappers = request.UseModelBinding
                    ? BuildModelBindingMappers(
                        request.ExtendedProperties,
                        await GetMappersCachedAsync(request.TransactionEntityName, connection, cancellationToken))
                    : await GetMappersCachedAsync(request.TransactionEntityName, connection, cancellationToken);

                    if (!request.UseModelBinding && mainMappers.Count == 0)
                        throw new InvalidOperationException($"No field mappings found for table '{request.TransactionEntityName}'.");

                    if (request.UseModelBinding)
                        await EnsureModelBindingMetadataAsync(request.TransactionEntityName, connection, cancellationToken);

                    var operationType = DetermineOperationType(request.ExtendedProperties, mainMappers);
                    object mainRecordId;

                    if (operationType == "Insert")
                    {
                        mainRecordId = await ProcessInsertAsync(
                            request.TransactionEntityName, request.ExtendedProperties, mainMappers,
                            transactionId, userId, correlationId, request.IpAddress,
                            connection, transaction, request.UseModelBinding, cancellationToken);
                    }
                    else
                    {
                        mainRecordId = await ProcessUpdateAsync(
                            request.TransactionEntityName, request.ExtendedProperties, mainMappers,
                            transactionId, userId, correlationId, request.IpAddress,
                            connection, transaction, request.UseModelBinding, cancellationToken);
                    }

                    if (request.RenProps?.Count > 0)
                    {
                        await ProcessChildRecordsAsync(
                            request.RenProps, mainRecordId.ToString()!, connection, transaction,
                            transactionId, userId, correlationId, request.IpAddress,
                            1, request.UseModelBinding, cancellationToken);
                    }

                    if (request.DelProps?.Count > 0)
                    {
                        await ProcessDeleteOperationsAsync(
                            request.DelProps, transactionId, userId, correlationId, request.IpAddress,
                            connection, transaction, cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);

                    return new TransactionResult
                    {
                        Success = true,
                        TransactionId = transactionId,
                        Message = $"Committed: {operationType}",
                        Data = new Dictionary<string, object> { ["RENGUID"] = mainRecordId }
                    };
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }, strategy, _logger, maxAttempts: 3, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tx:{TxId} Rolled back due to error", transactionId);
            return Fail(SqlErrorTranslator.ToSafeMessage(ex), transactionId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // CORE MUTATION METHODS
    // ═══════════════════════════════════════════════════════════════════

    private async Task<object> ProcessInsertAsync(
        string tableName,
        Dictionary<string, object> data,
        IReadOnlyList<FieldMapper> mappers,
        Guid transactionId, string userId, string correlationId, string? ipAddress,
        IDbConnection connection, IDbTransaction transaction,
        bool useModelBinding,
        CancellationToken ct)
    {
        await _tableNameValidator.EnsureAllowedAsync(tableName, ct);
        var strategy = _connectionFactory.GetCurrentStrategy();

        var pkMapper = GetPrimaryKeyMapper(mappers);
        var idColumn = pkMapper.ColumnName;
        var idField = pkMapper.FieldName;

        var columns = new List<string>();
        var valuePlaceholders = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (useModelBinding)
        {
            // FIXED: mass-assignment. Every incoming key is now validated as a
            // safe identifier AND checked against the table's real column set
            // before it is allowed to become part of the SQL text.
            var allowedColumns = await GetAllowedColumnNamesAsync(tableName, connection, ct);

            foreach (var kv in data)
            {
                var key = kv.Key;
                if (key.Equals(idField, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.CreatedAt, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.CreatedBy, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.UpdatedAt, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.UpdatedBy, StringComparison.OrdinalIgnoreCase)
                    || key.Equals("renProps", StringComparison.OrdinalIgnoreCase))
                    continue;

                _sqlGuardian.ValidateFieldName(key); // throws SqlValidationException if shape is unsafe

                if (!allowedColumns.Contains(key))
                    throw new SqlValidationException($"Column '{key}' is not an allowed target for table '{tableName}'.");

                columns.Add(strategy.QuoteIdentifier(key));
                valuePlaceholders.Add(strategy.NormalizeParameterName(key));

                var mapperForField = mappers.FirstOrDefault(m => m.FieldName.Equals(key, StringComparison.OrdinalIgnoreCase));
                parameters[key] = DataTypeConverter.CoerceValue(kv.Value, mapperForField?.DataType);
            }
        }
        else
        {
            var insertableMappers = mappers.Where(m => m.AllowUpdate && (m.Properties == null || !m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var mapper in insertableMappers)
            {
                if (!data.TryGetValue(mapper.FieldName, out var rawVal)) continue;

                columns.Add(strategy.QuoteIdentifier(mapper.ColumnName));
                valuePlaceholders.Add(strategy.NormalizeParameterName(mapper.FieldName));
                
                var resolved = ResolveValue(rawVal, null);
                parameters[mapper.FieldName] = DataTypeConverter.CoerceValue(resolved, mapper.DataType);
            }
        }

        var hasCreatedAt = mappers.Any(m => m.ColumnName.Equals(_auditColumns.CreatedAt, StringComparison.OrdinalIgnoreCase) || m.FieldName.Equals(_auditColumns.CreatedAt, StringComparison.OrdinalIgnoreCase));
        var hasCreatedBy = mappers.Any(m => m.ColumnName.Equals(_auditColumns.CreatedBy, StringComparison.OrdinalIgnoreCase) || m.FieldName.Equals(_auditColumns.CreatedBy, StringComparison.OrdinalIgnoreCase));

        if (hasCreatedAt)
        {
            columns.Add(strategy.QuoteIdentifier(_auditColumns.CreatedAt));
            valuePlaceholders.Add(strategy.CurrentTimestampExpression);
        }
        if (hasCreatedBy)
        {
            columns.Add(strategy.QuoteIdentifier(_auditColumns.CreatedBy));
            valuePlaceholders.Add(strategy.NormalizeParameterName("sysUserId"));
            parameters["sysUserId"] = userId;
        }

        var baseInsertSql = $"INSERT INTO {strategy.QuoteIdentifier(tableName)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", valuePlaceholders)})";
        var finalSql = strategy.BuildInsertReturningKey(baseInsertSql, idColumn);

        _logger.LogDebug("Tx:{TxId} INSERT SQL: {Sql}", transactionId, finalSql);

        var paramObj = new DynamicParameters();
        foreach (var kv in parameters) paramObj.Add(strategy.NormalizeParameterName(kv.Key), UnwrapValue(kv.Value));

        object newlyGeneratedId;
        if (strategy.SupportsInlineOutputClause)
        {
            string rawOutParamName = "RETURN_ID";
            string outParamName = strategy.NormalizeParameterName(rawOutParamName);
            var idColumnType = pkMapper.DataType != null && !pkMapper.DataType.Equals("object", StringComparison.OrdinalIgnoreCase)
                ? strategy.GetDbType(pkMapper.DataType) 
                : DbType.Int64;

            paramObj.Add(outParamName, dbType: idColumnType, direction: ParameterDirection.Output, size: 64);

            await connection.ExecuteAsync(new CommandDefinition(finalSql, paramObj, transaction, cancellationToken: ct));
            newlyGeneratedId = paramObj.Get<object>(rawOutParamName)
                ?? throw new QueryExecutionException("Database failed to return auto-generated identity token via output parameters.");
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(finalSql, paramObj, transaction, cancellationToken: ct));
            var identityFallbackSql = strategy.BuildLastInsertIdQuery();
            newlyGeneratedId = await connection.ExecuteScalarAsync<object>(new CommandDefinition(identityFallbackSql, transaction, cancellationToken: ct))
                ?? throw new QueryExecutionException("Failed to recover connection identity footprint tracker token.");
        }

        newlyGeneratedId = UnwrapValue(newlyGeneratedId)!;
        await AuditAsync(transactionId, tableName, newlyGeneratedId.ToString()!, AuditOperation.Insert, null, data, userId, correlationId, ipAddress, ct);
        return newlyGeneratedId;
    }

    private async Task<object> ProcessUpdateAsync(string tableName, Dictionary<string, object> data, IReadOnlyList<FieldMapper> mappers, Guid transactionId, string userId, string correlationId, string? ipAddress, IDbConnection connection, IDbTransaction transaction, bool useModelBinding, CancellationToken ct)
    {
        await _tableNameValidator.EnsureAllowedAsync(tableName, ct);
        var strategy = _connectionFactory.GetCurrentStrategy();

        var pkMapper = GetPrimaryKeyMapper(mappers);
        var idColumn = pkMapper.ColumnName;
        var idField = pkMapper.FieldName;

        if (!data.TryGetValue(idField, out var rawIdValue) || rawIdValue == null)
            throw new InvalidOperationException($"Missing primary key field '{idField}' for update.");

        var idValue = UnwrapValue(rawIdValue)!;
        var beforeData = await ReadRowSnapshotAsync(tableName, idColumn, idValue, connection, transaction, ct);

        var assignments = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (useModelBinding)
        {
            // FIXED: same allowlist + identifier-shape validation as insert path.
            var allowedColumns = await GetAllowedColumnNamesAsync(tableName, connection, ct);

            foreach (var kv in data)
            {
                var key = kv.Key;
                if (key.Equals(idField, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.CreatedAt, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(_auditColumns.CreatedBy, StringComparison.OrdinalIgnoreCase)
                    || key.Equals("renProps", StringComparison.OrdinalIgnoreCase))
                    continue;

                _sqlGuardian.ValidateFieldName(key);

                if (!allowedColumns.Contains(key))
                    throw new SqlValidationException($"Column '{key}' is not an allowed target for table '{tableName}'.");

                assignments.Add($"{strategy.QuoteIdentifier(key)} = {strategy.NormalizeParameterName(key)}");

                var mapperForField = mappers.FirstOrDefault(m => m.FieldName.Equals(key, StringComparison.OrdinalIgnoreCase));
                parameters[key] = DataTypeConverter.CoerceValue(kv.Value, mapperForField?.DataType);
            }
        }
        else
        {
            var updateableMappers = mappers.Where(m => m.AllowUpdate && (m.Properties == null || !m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var mapper in updateableMappers)
            {
                if (!data.TryGetValue(mapper.FieldName, out var rawVal)) continue;
                assignments.Add($"{strategy.QuoteIdentifier(mapper.ColumnName)} = {strategy.NormalizeParameterName(mapper.FieldName)}");
                
                var resolved = ResolveValue(rawVal, null);
                parameters[mapper.FieldName] = DataTypeConverter.CoerceValue(resolved, mapper.DataType);
            }
        }

        if (assignments.Count == 0)
            throw new InvalidOperationException("Update request contains no updatable fields.");

        var hasUpdatedAt = mappers.Any(m => m.ColumnName.Equals(_auditColumns.UpdatedAt, StringComparison.OrdinalIgnoreCase) || m.FieldName.Equals(_auditColumns.UpdatedAt, StringComparison.OrdinalIgnoreCase));
        var hasUpdatedBy = mappers.Any(m => m.ColumnName.Equals(_auditColumns.UpdatedBy, StringComparison.OrdinalIgnoreCase) || m.FieldName.Equals(_auditColumns.UpdatedBy, StringComparison.OrdinalIgnoreCase));

        if (hasUpdatedAt)
        {
            assignments.Add($"{strategy.QuoteIdentifier(_auditColumns.UpdatedAt)} = {strategy.CurrentTimestampExpression}");
        }
        if (hasUpdatedBy)
        {
            assignments.Add($"{strategy.QuoteIdentifier(_auditColumns.UpdatedBy)} = {strategy.NormalizeParameterName("sysUserId")}");
            parameters["sysUserId"] = userId;
        }
        parameters["targetRecordId"] = idValue;

        var sql = $"UPDATE {strategy.QuoteIdentifier(tableName)} SET {string.Join(", ", assignments)} WHERE {strategy.QuoteIdentifier(idColumn)} = {strategy.NormalizeParameterName("targetRecordId")}";
        var paramObj = new DynamicParameters();
        foreach (var kv in parameters) paramObj.Add(strategy.NormalizeParameterName(kv.Key), UnwrapValue(kv.Value));

        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, paramObj, transaction, cancellationToken: ct));
        if (affected == 0)
            throw new QueryExecutionException($"Update affected 0 rows — record '{idValue}' may not exist in '{tableName}'.");

        await AuditAsync(transactionId, tableName, idValue.ToString()!, AuditOperation.Update, beforeData, data, userId, correlationId, ipAddress, ct);
        return idValue;
    }

    internal static FieldMapper GetPrimaryKeyMapper(IReadOnlyList<FieldMapper> mappers)
    {
        var autoGen = mappers.FirstOrDefault(m => m.Properties?.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase) == true);
        if (autoGen != null) return autoGen;

        var pk = mappers.FirstOrDefault(m => m.Properties?.Contains("PrimaryKey", StringComparison.OrdinalIgnoreCase) == true);
        if (pk != null) return pk;

        var idMapper = mappers.FirstOrDefault(m => m.FieldName.Equals("id", StringComparison.OrdinalIgnoreCase) 
            || m.ColumnName.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idMapper != null) return idMapper;

        return new FieldMapper
        {
            FieldName = "id",
            ColumnName = "id",
            DataType = "object",
            AllowUpdate = false
        };
    }

    private async Task ProcessDeleteOperationsAsync(Dictionary<string, List<Dictionary<string, object>>> delProps, Guid transactionId, string userId, string correlationId, string? ipAddress, IDbConnection connection, IDbTransaction transaction, CancellationToken ct)
    {
        var strategy = _connectionFactory.GetCurrentStrategy();
        foreach (var delBlock in delProps)
        {
            var tableName = delBlock.Key;
            await _tableNameValidator.EnsureAllowedAsync(tableName, ct);

            var mappers = await GetMappersCachedAsync(tableName, connection, ct);
            if (mappers.Count == 0)
                throw new SqlValidationException($"Table '{tableName}' is not registered for transaction access.");

            var pkMapper = GetPrimaryKeyMapper(mappers);
            var idColumn = pkMapper.ColumnName;
            var idField = pkMapper.FieldName;

            foreach (var record in delBlock.Value)
            {
                if (!record.TryGetValue(idField, out var idVal) || idVal == null)
                    throw new SqlValidationException($"Delete operations require primary key field '{idField}' for table '{tableName}'.");

                var idValue = UnwrapValue(idVal)!;
                var beforeData = await ReadRowSnapshotAsync(tableName, idColumn, idValue, connection, transaction, ct);

                var sql = $"DELETE FROM {strategy.QuoteIdentifier(tableName)} WHERE {strategy.QuoteIdentifier(idColumn)} = {strategy.NormalizeParameterName("delId")}";
                var parameters = new DynamicParameters();
                parameters.Add(strategy.NormalizeParameterName("delId"), idValue);

                var affected = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
                if (affected == 0)
                    throw new QueryExecutionException($"Delete affected 0 rows — record '{idValue}' may not exist in '{tableName}'.");

                await AuditAsync(transactionId, tableName, idValue.ToString()!, AuditOperation.Delete, beforeData, null, userId, correlationId, ipAddress, ct);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // CHILD HIERARCHIES & STRUCTURAL UTILITIES
    // ═══════════════════════════════════════════════════════════════════

    private async Task ProcessChildRecordsAsync(Dictionary<string, List<Dictionary<string, object>>> renProps, string parentId, IDbConnection connection, IDbTransaction transaction, Guid transactionId, string userId, string correlationId, string? ipAddress, int depth, bool useModelBinding, CancellationToken ct)
    {
        if (depth > MaxRecursionDepth)
            throw new InvalidOperationException($"Maximum recursion depth ({MaxRecursionDepth}) exceeded.");

        foreach (var childBlock in renProps)
        {
            var childTable = childBlock.Key;
            await _tableNameValidator.EnsureAllowedAsync(childTable, ct);

            var childMappers = useModelBinding && childBlock.Value.Count > 0
                ? BuildModelBindingMappers(childBlock.Value[0], await GetMappersCachedAsync(childTable, connection, ct))
                : await GetMappersCachedAsync(childTable, connection, ct);

            if (!useModelBinding && childMappers.Count == 0)
                throw new InvalidOperationException($"No field mappings found for child table '{childTable}'.");

            foreach (var record in childBlock.Value)
            {
                ReplaceRenGuidTokens(record, parentId);
                var op = DetermineOperationType(record, childMappers);
                object childRecordId;

                if (op == "Insert")
                {
                    childRecordId = await ProcessInsertAsync(childTable, record, childMappers, transactionId, userId, correlationId, ipAddress, connection, transaction, useModelBinding, ct);
                }
                else
                {
                    childRecordId = await ProcessUpdateAsync(childTable, record, childMappers, transactionId, userId, correlationId, ipAddress, connection, transaction, useModelBinding, ct);
                }

                if (record.TryGetValue("renProps", out var nested) && nested is Dictionary<string, List<Dictionary<string, object>>> deepRenProps)
                {
                    await ProcessChildRecordsAsync(deepRenProps, childRecordId.ToString()!, connection, transaction, transactionId, userId, correlationId, ipAddress, depth + 1, useModelBinding, ct);
                }
            }
        }
    }

    private static void ReplaceRenGuidTokens(Dictionary<string, object> record, string parentId)
    {
        foreach (var key in record.Keys.ToList())
        {
            if (record[key] is string strVal && strVal.Contains("|RENGUID|", StringComparison.OrdinalIgnoreCase))
            {
                record[key] = strVal.Replace("|RENGUID|", parentId, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static IReadOnlyList<FieldMapper> BuildModelBindingMappers(
        Dictionary<string, object> properties,
        IReadOnlyList<FieldMapper>? metadataMappers)
    {
        var pkMapper = metadataMappers is { Count: > 0 }
            ? GetPrimaryKeyMapper(metadataMappers)
            : null;

        var isAutoGen = pkMapper?.Properties?.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase) == true;

        return properties.Keys.Select(key => new FieldMapper
        {
            FieldName = key,
            ColumnName = key,
            DataType = "object",
            AllowUpdate = true,
            Properties = pkMapper is not null && key.Equals(pkMapper.FieldName, StringComparison.OrdinalIgnoreCase) && isAutoGen
                ? "AutoGenerated"
                : null
        }).ToList();
    }

    private async Task EnsureModelBindingMetadataAsync(string tableName, IDbConnection connection, CancellationToken ct)
    {
        var mappers = await GetMappersCachedAsync(tableName, connection, ct);
        if (mappers.Count == 0)
        {
            throw new ConfigurationException(
                $"Model binding requires field mapper metadata for table '{tableName}'. " +
                "Register mappings or set useModelBinding to false.");
        }
    }

    private static string DetermineOperationType(Dictionary<string, object> properties, IReadOnlyList<FieldMapper> mappers)
    {
        var pkMapper = GetPrimaryKeyMapper(mappers);
        var isAutoGen = pkMapper.Properties?.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase) == true;

        if (properties.TryGetValue(pkMapper.FieldName, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
        {
            var strVal = val.ToString();
            if (isAutoGen)
            {
                if (strVal == "0" || string.Equals(strVal, Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase))
                    return "Insert";
            }
            return "Update";
        }
        return "Insert";
    }

    // CHANGED: process-level cache (IMemoryCache, 10 min TTL) instead of a
    // dictionary that was created fresh on every TransactionAsync call.
    private async Task<IReadOnlyList<FieldMapper>> GetMappersCachedAsync(string tableName, IDbConnection connection, CancellationToken ct)
    {
        var connectionName = _connectionFactory.GetCurrentOptions().Name ?? "default";
        var cacheKey = $"fieldmappers::{connectionName}::{tableName}";
        if (_mapperCache.TryGetValue(cacheKey, out IReadOnlyList<FieldMapper>? cached) && cached is not null)
            return cached;

        var mappers = await _fieldMapperRepository.GetFieldMappersAsync(tableName, connection, ct);
        _mapperCache.Set(cacheKey, mappers, MapperCacheTtl);
        return mappers;
    }

    // NEW: derives the allowlisted column set for model-binding requests from
    // the same metadata source used for non-model-binding inserts/updates.
    // This is what closes the mass-assignment gap.
    private async Task<HashSet<string>> GetAllowedColumnNamesAsync(string tableName, IDbConnection connection, CancellationToken ct)
    {
        var connectionName = _connectionFactory.GetCurrentOptions().Name ?? "default";
        var cacheKey = $"allowedcolumns::{connectionName}::{tableName}";
        if (_mapperCache.TryGetValue(cacheKey, out HashSet<string>? cached) && cached is not null)
            return cached;

        var mappers = await _fieldMapperRepository.GetFieldMappersAsync(tableName, connection, ct);
        if (mappers.Count == 0)
        {
            throw new ConfigurationException($"No field mapping metadata is configured for table '{tableName}'. In model-binding mode, metadata is required to protect against mass-assignment.");
        }

        var allowed = mappers
            .Where(m => m.AllowUpdate)
            .Select(m => m.FieldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _mapperCache.Set(cacheKey, allowed, MapperCacheTtl);
        return allowed;
    }

    private async Task<Dictionary<string, object?>?> ReadRowSnapshotAsync(string tableName, string idColumn, object idValue, IDbConnection connection, IDbTransaction transaction, CancellationToken ct)
    {
        var strategy = _connectionFactory.GetCurrentStrategy();
        var sql = $"SELECT * FROM {strategy.QuoteIdentifier(tableName)} WHERE {strategy.QuoteIdentifier(idColumn)} = {strategy.NormalizeParameterName("id")}";
        var parameters = new DynamicParameters();
        parameters.Add(strategy.NormalizeParameterName("id"), idValue);

        var result = await connection.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        if (result == null) return null;
        return ((IDictionary<string, object>)result).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
    }

    private static object? ResolveValue(object? rawValue, string? defaultValue)
    {
        if (rawValue != null) return rawValue;
        return defaultValue?.ToLowerInvariant() switch
        {
            "now" or "|todaydate|" => DateTime.UtcNow,
            "|renguid|" => Guid.NewGuid().ToString(),
            _ => null
        };
    }

    private static object? UnwrapValue(object? value)
    {
        if (value == null) return DBNull.Value;
        if (value is JsonElement json)
        {
            value = json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number => json.TryGetInt64(out var l) ? l : json.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => json.GetRawText()
            };
        }
        if (value is string str && str.Length > 10 && str.Contains('T') && (str.EndsWith('Z') || str.Contains('+')) && DateTime.TryParse(str, out var parsedDate))
        {
            return parsedDate;
        }
        return value ?? DBNull.Value;
    }

    // CHANGED: audit call no longer takes an IDbConnection/IDbTransaction — the
    // AuditService now owns its own async write path (see AuditService.cs) and
    // no longer needs to share the caller's transaction/connection.
    private async Task AuditAsync(Guid transactionId, string tableName, string recordId, AuditOperation operation, Dictionary<string, object?>? beforeData, Dictionary<string, object>? afterData, string userId, string correlationId, string? ipAddress, CancellationToken ct)
    {
        var changes = afterData?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            ?? beforeData?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            ?? [];
        var connectionName = _connectionFactory.GetCurrentOptions().Name;
        await _auditService.LogAsync(transactionId, tableName, operation, changes, userId, ipAddress, ct, connectionName);
    }

    private static TransactionResult Fail(string message, Guid transactionId) => new()
    {
        Success = false,
        TransactionId = transactionId,
        Message = message
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
