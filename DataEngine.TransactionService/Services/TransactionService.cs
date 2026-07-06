using DataEngine.Core.Auditing;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Security;
using DataEngine.ReaderService.Enums;
using DataEngine.ReaderService.Services;
using DataEngine.TransactionService.Domain;
using DataEngine.TransactionService.Interfaces;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace DataEngine.TransactionService.Services;

/// <summary>
/// Operational transaction service executing complex, multi-level parent-child modifications inside an atomic scope.
/// </summary>
public sealed class TransactionService(
    DatabaseConnectionFactory connectionFactory,
    IFieldMapperRepository fieldMapperRepository,
    IAuditService auditService,
    IValidationService validationService,
    ITableNameValidator tableNameValidator,
    ILogger<TransactionService> logger) : ITransactionService
{
    private readonly DatabaseConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    private readonly IFieldMapperRepository _fieldMapperRepository = fieldMapperRepository ?? throw new ArgumentNullException(nameof(fieldMapperRepository));
    private readonly IAuditService _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
    private readonly IValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly ITableNameValidator _tableNameValidator = tableNameValidator ?? throw new ArgumentNullException(nameof(tableNameValidator));
    private readonly ILogger<TransactionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TransactionEntityName))
        {
            return new TransactionResult { Success = false, Message = "Invalid transaction payload validation targets." };
        }

        string transactionId = string.IsNullOrWhiteSpace(request.TransactionId)
            ? Guid.NewGuid().ToString()
            : request.TransactionId;

        await using DbConnection connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? transactionId : request.CorrelationId;
            var userId = string.IsNullOrWhiteSpace(request.UserId) ? "System" : request.UserId;
            _logger.LogInformation("Beginning execution sequence for TransactionId: {TxId} targeting {Table} (ModelBinding: {UseModelBinding})", transactionId, request.TransactionEntityName, request.UseModelBinding);

            await _tableNameValidator.EnsureAllowedAsync(request.TransactionEntityName, ct);
            var validationResult = await _validationService.ValidateAsync(request.TransactionEntityName, request.ExtendedProperties, null);
            if (!validationResult.IsValid)
            {
                return new TransactionResult
                {
                    Success = false,
                    TransactionId = transactionId,
                    Message = "Transaction validation failed.",
                    ValidationErrors = validationResult.Errors.Select(e => new ValidationError { FieldName = e.FieldName, ErrorMessage = e.ErrorMessage, Rule = e.Rule }).ToList()
                };
            }

            var mappersCache = new Dictionary<string, List<FieldMapper>>(StringComparer.Ordinal);

            // When UseModelBinding is true, we skip field mapper lookup for the main entity
            List<FieldMapper> mainMappers;
            if (request.UseModelBinding)
            {
                mainMappers = BuildModelBindingMappers(request.ExtendedProperties);
            }
            else
            {
                mainMappers = await GetMappersCachedAsync(request.TransactionEntityName, connection, mappersCache);
                if (mainMappers.Count == 0)
                {
                    throw new InvalidOperationException($"No field mappings were found for table '{request.TransactionEntityName}'.");
                }
            }

            string operationType = DetermineOperationType(request.ExtendedProperties, mainMappers);

            object mainRecordId;
            if (operationType == "Insert")
            {
                mainRecordId = await ProcessInsertAsync(request.TransactionEntityName, request.ExtendedProperties, mainMappers, transactionId, userId, correlationId, request.IpAddress, connection, transaction, ct, request.UseModelBinding);
            }
            else
            {
                mainRecordId = await ProcessUpdateAsync(request.TransactionEntityName, request.ExtendedProperties, mainMappers, transactionId, userId, correlationId, request.IpAddress, connection, transaction, ct, request.UseModelBinding);
            }

            if (request.RenProps != null && request.RenProps.Count > 0)
            {
                await ProcessChildRecordsAsync(request.RenProps, mainRecordId.ToString()!, connection, transaction, mappersCache, transactionId, userId, correlationId, request.IpAddress, 1, ct, request.UseModelBinding);
            }

            if (request.DelProps != null && request.DelProps.Count > 0)
            {
                await ProcessDeleteOperationsAsync(request.DelProps, transactionId, userId, correlationId, request.IpAddress, connection, transaction, ct);
            }

            await transaction.CommitAsync(ct);

            return new TransactionResult
            {
                Success = true,
                TransactionId = transactionId,
                Message = $"Transaction committed successfully. Node affected: {operationType}.",
                Data = new Dictionary<string, object> { { "RENGUID", mainRecordId } }
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "FATAL TRANSMISSION CRASH: Rolling back execution scope. TxId: {TxId}", transactionId);
            return new TransactionResult { Success = false, TransactionId = transactionId, Message = "Transaction rolled back due to an internal processing error." };
        }
    }

    /// <summary>
    /// Builds synthetic field mappers directly from model properties when UseModelBinding is enabled.
    /// Property names map 1:1 to column names (case-sensitive).
    /// </summary>
    private static List<FieldMapper> BuildModelBindingMappers(Dictionary<string, object> properties)
    {
        var mappers = new List<FieldMapper>();
        foreach (var key in properties.Keys)
        {
            mappers.Add(new FieldMapper
            {
                FieldName = key,
                ColumnName = key,
                DataType = "object", // Generic fallback for model binding; DbParameter infers from runtime value
                AllowUpdate = true,
                Properties = key.Equals("id", StringComparison.OrdinalIgnoreCase) ? "AutoGenerated" : null,
                DefaultValue = null
            });
        }
        return mappers;
    }

    private string DetermineOperationType(Dictionary<string, object> properties, List<FieldMapper> mappers)
    {
        var autoGenMapper = mappers.FirstOrDefault(m => m.Properties != null && m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase));
        if (autoGenMapper != null && properties.TryGetValue(autoGenMapper.FieldName, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()) && val.ToString() != "0")
        {
            return "Update";
        }

        if (properties.TryGetValue("id", out var fallbackId) && fallbackId != null && !string.IsNullOrWhiteSpace(fallbackId.ToString()) && fallbackId.ToString() != Guid.Empty.ToString())
        {
            return "Update";
        }

        return "Insert";
    }

    private async Task<object> ProcessInsertAsync(string tableName, Dictionary<string, object> data, List<FieldMapper> mappers, string transactionId, string userId, string correlationId, string? ipAddress, DbConnection conn, DbTransaction tx, CancellationToken ct, bool useModelBinding = false)
    {
        await _tableNameValidator.EnsureAllowedAsync(tableName, ct);
        if (!useModelBinding && mappers.Count == 0)
        {
            throw new InvalidOperationException($"No field mappings were found for table '{tableName}'.");
        }

        var activeMappers = mappers.Where(m => m.Properties == null || !m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase)).ToList();

        var columns = new List<string>();
        var values = new List<string>();
        var commandParams = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var mapper in activeMappers)
        {
            columns.Add($"`{mapper.ColumnName}`");
            values.Add($"@{mapper.FieldName}");

            data.TryGetValue(mapper.FieldName, out var rawVal);
            commandParams[$"@{mapper.FieldName}"] = ResolveTokens(rawVal, mapper.DefaultValue, null);
        }

        if (!columns.Contains("`created_at`"))
        {
            columns.Add("`created_at`");
            values.Add("NOW()");
        }

        if (!columns.Contains("`created_by`"))
        {
            columns.Add("`created_by`");
            values.Add("@sysUserId");
            commandParams["@sysUserId"] = userId;
        }

        string sql = $"INSERT INTO `{tableName}` ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)}); SELECT LAST_INSERT_ID();";

        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;

        foreach (var kp in commandParams)
        {
            var p = command.CreateParameter();
            p.ParameterName = kp.Key;

            // FIX: Detect if ResolveTokens returned a JsonElement and unwrap it to a primitive type
            if (kp.Value is JsonElement jsonElement)
            {
                p.Value = jsonElement.ValueKind switch
                {
                    JsonValueKind.String => jsonElement.GetString(),
                    JsonValueKind.Number => jsonElement.TryGetInt64(out long l) ? l : jsonElement.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => DBNull.Value,
                    _ => jsonElement.GetRawText() // Fallback to raw string string text for nested items
                };
            }
            else
            {
                p.Value = kp.Value ?? DBNull.Value;
            }

            if (p.Value is string strVal && strVal.Contains('T') && (strVal.EndsWith('Z') || strVal.Contains('+')))
            {
                if (DateTime.TryParse(strVal, out DateTime parsedDate))
                {
                    p.Value = parsedDate; // MySqlConnector converts this cleanly into a MySQL-compatible format
                }
            }

            command.Parameters.Add(p);
        }

        object scalar = null!;
        try
        {
            // Execute the insert pipeline safely inside a try block
            scalar = await command.ExecuteScalarAsync(ct);
        }
        catch (DbException dbEx)
        {
            // Captures direct MySQL syntax, duplicate key, or connection timeout errors
            _logger.LogError(dbEx, "DATABASE EXECUTION ERROR: Failed to execute insert payload on table '{TableName}'. SQL: {SqlText}", tableName, sql);
            throw; // Re-throw to propagate back up to the transaction resiliency policy
        }
        catch (Exception ex)
        {
            // Captures parsing or system level driver aborts
            _logger.LogError(ex, "FATAL INTERNALS ENGINE FAILURE: Unexpected crash during insertion command processing.");
            throw;
        }

        object insertedId;
        if (scalar == null || Convert.ToInt64(scalar) == 0)
        {
            if (commandParams.TryGetValue("@id", out var exactGuid) && exactGuid != null) insertedId = exactGuid;
            else insertedId = Guid.NewGuid().ToString();
        }
        else
        {
            insertedId = scalar;
        }

        await _auditService.RecordAsync(
            new AuditEntry(transactionId, tableName, insertedId.ToString()!, AuditOperation.Create, null, data, userId, correlationId, ipAddress),
            conn,
            tx,
            ct);

        return insertedId;
    }

    private async Task<object> ProcessUpdateAsync(string tableName, Dictionary<string, object> data, List<FieldMapper> mappers, string transactionId, string userId, string correlationId, string? ipAddress, DbConnection conn, DbTransaction tx, CancellationToken ct, bool useModelBinding = false)
    {
        await _tableNameValidator.EnsureAllowedAsync(tableName, ct);
        if (!useModelBinding && mappers.Count == 0)
        {
            throw new InvalidOperationException($"No field mappings were found for table '{tableName}'.");
        }

        var idMapper = mappers.FirstOrDefault(m => m.Properties != null && m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase));
        string idColumn = idMapper != null ? idMapper.ColumnName : "id";
        string idField = idMapper != null ? idMapper.FieldName : "id";

        if (!data.TryGetValue(idField, out var rawIdValue) || rawIdValue == null)
        {
            throw new InvalidOperationException($"Missing required primary key element lookup value fields inside targeting table framework update: {idField}");
        }

        // FIX 1: Safely unwrap idValue using the centralized normalizer helper
        object idValue = UnwrapAndNormalizeValue(rawIdValue)!;

        var beforeData = await ReadRowSnapshotAsync(tableName, idColumn, idValue, conn, tx, ct);
        var updateableMappers = mappers.Where(m => m.AllowUpdate && (m.Properties == null || !m.Properties.Contains("AutoGenerated", StringComparison.OrdinalIgnoreCase))).ToList();

        var assignmentLines = new List<string>();
        var commandParams = new Dictionary<string, object?>(StringComparer.Ordinal);

        // FIX 2: Fully support direct UseModelBinding property mapping fallback
        if (useModelBinding && updateableMappers.Count == 0)
        {
            foreach (var kp in data)
            {
                string key = kp.Key;
                if (key.Equals(idField, StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("created_by", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Do not modify primary keys or immutable records
                }
                assignmentLines.Add($"`{key}` = @{key}");
                commandParams[$"@{key}"] = kp.Value;
            }
        }
        else
        {
            foreach (var mapper in updateableMappers)
            {
                if (data.TryGetValue(mapper.FieldName, out var rawVal))
                {
                    assignmentLines.Add($"`{mapper.ColumnName}` = @{mapper.FieldName}");
                    commandParams[$"@{mapper.FieldName}"] = ResolveTokens(rawVal, null, null);
                }
            }
        }

        // FIX 3: Harmonized tracking audit columns to match your exact snake_case table schema attributes
        assignmentLines.Add("`updated_at` = NOW()");
        assignmentLines.Add("`updated_by` = @sysUserId");
        commandParams["@sysUserId"] = userId;
        commandParams["@targetRecordId"] = idValue;

        string sql = $"UPDATE `{tableName}` SET {string.Join(", ", assignmentLines)} WHERE `{idColumn}` = @targetRecordId";

        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;

        foreach (var kp in commandParams)
        {
            var p = command.CreateParameter();
            p.ParameterName = kp.Key;
            // FIX 4: Centralized normalizer cleans up JsonElements and parses ISO Date strings perfectly
            p.Value = UnwrapAndNormalizeValue(kp.Value);
            command.Parameters.Add(p);
        }

        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (DbException dbEx)
        {
            _logger.LogError(dbEx, "DATABASE UPDATE FAILURE: Error executing payload on table '{TableName}'. Query Text: {SqlText}", tableName, sql);
            throw;
        }

        await _auditService.RecordAsync(
            new AuditEntry(transactionId, tableName, idValue.ToString()!, AuditOperation.Update, beforeData, data, userId, correlationId, ipAddress),
            conn,
            tx,
            ct);

        return idValue;
    }

    /// <summary>
    /// Centralized data normalization utility. Extracts pure data from JsonElements and handles ISO 8601 strings.
    /// </summary>
    private static object? UnwrapAndNormalizeValue(object? value)
    {
        if (value == null) return DBNull.Value;

        if (value is JsonElement jsonElement)
        {
            value = jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number => jsonElement.TryGetInt64(out long l) ? l : jsonElement.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => jsonElement.GetRawText()
            };
        }

        if (value is string strVal && strVal.Contains('T') && (strVal.EndsWith('Z') || strVal.Contains('+')))
        {
            if (DateTime.TryParse(strVal, out DateTime parsedDate))
            {
                return parsedDate;
            }
        }

        return value ?? DBNull.Value;
    }

    /// <summary>
    /// Processes recursive child rows up to a strict limit depth of 5 levels deep.
    /// </summary>
    private async Task ProcessChildRecordsAsync(
        Dictionary<string, List<Dictionary<string, object>>> renProps,
        string parentId,
        DbConnection conn,
        DbTransaction tx,
        Dictionary<string, List<FieldMapper>> mappersCache,
        string transactionId,
        string userId,
        string correlationId,
        string? ipAddress,
        int depth,
        CancellationToken ct,
        bool useModelBinding = false)
    {
        if (depth > 5)
        {
            throw new InvalidOperationException("Execution truncated. Loop passed max recursion ceiling boundaries of 5 levels.");
        }

        foreach (var childBlock in renProps)
        {
            string childTable = childBlock.Key;
            await _tableNameValidator.EnsureAllowedAsync(childTable, ct);

            List<FieldMapper> childMappers;
            if (useModelBinding)
            {
                // For model binding, build mappers from the first record's properties (all records in a block share the same schema)
                if (childBlock.Value.Count > 0)
                {
                    childMappers = BuildModelBindingMappers(childBlock.Value[0]);
                }
                else
                {
                    continue;
                }
            }
            else
            {
                childMappers = await GetMappersCachedAsync(childTable, conn, mappersCache);
                if (childMappers.Count == 0)
                {
                    throw new InvalidOperationException($"No field mappings were found for table '{childTable}'.");
                }
            }

            foreach (var record in childBlock.Value)
            {
                string op = DetermineOperationType(record, childMappers);
                object childRecordId;

                foreach (var kvp in record.ToList())
                {
                    if (kvp.Value?.ToString() == "|RENGUID|")
                    {
                        record[kvp.Key] = parentId;
                    }
                }

                if (op == "Insert")
                {
                    childRecordId = await ProcessInsertAsync(childTable, record, childMappers, transactionId, userId, correlationId, ipAddress, conn, tx, ct, useModelBinding);
                }
                else
                {
                    childRecordId = await ProcessUpdateAsync(childTable, record, childMappers, transactionId, userId, correlationId, ipAddress, conn, tx, ct, useModelBinding);
                }

                if (record.TryGetValue("renProps", out var nestedObject) && nestedObject is Dictionary<string, List<Dictionary<string, object>>> deepRenProps)
                {
                    await ProcessChildRecordsAsync(deepRenProps, childRecordId.ToString()!, conn, tx, mappersCache, transactionId, userId, correlationId, ipAddress, depth + 1, ct, useModelBinding);
                }
            }
        }
    }

    /// <summary>
    /// Executes cascading parameterized row deletions across specified table targets.
    /// </summary>
    private async Task ProcessDeleteOperationsAsync(
        Dictionary<string, List<Dictionary<string, object>>> delProps,
        string transactionId,
        string userId,
        string correlationId,
        string? ipAddress,
        DbConnection conn,
        DbTransaction tx,
        CancellationToken ct)
    {
        foreach (var delBlock in delProps)
        {
            string tableName = delBlock.Key;
            await _tableNameValidator.EnsureAllowedAsync(tableName, ct);

            foreach (var record in delBlock.Value)
            {
                if (!record.TryGetValue("id", out var idVal) || idVal == null)
                {
                    throw new InvalidOperationException("Delete operations payloads must explicitly define target row 'id' values.");
                }

                var beforeData = await ReadRowSnapshotAsync(tableName, "id", idVal, conn, tx, ct);
                string sql = $"DELETE FROM `{tableName}` WHERE `id` = @delId";

                await using var command = conn.CreateCommand();
                command.CommandText = sql;
                command.Transaction = tx;

                var p = command.CreateParameter();
                p.ParameterName = "@delId";
                p.Value = idVal;
                command.Parameters.Add(p);

                await command.ExecuteNonQueryAsync(ct);

                await _auditService.RecordAsync(
                    new AuditEntry(transactionId, tableName, idVal.ToString()!, AuditOperation.Delete, beforeData, null, userId, correlationId, ipAddress),
                    conn,
                    tx,
                    ct);
            }
        }
    }

    private async Task<Dictionary<string, object>?> ReadRowSnapshotAsync(string tableName, string idColumn, object idValue, DbConnection conn, DbTransaction tx, CancellationToken ct)
    {
        string sql = $"SELECT * FROM `{tableName}` WHERE `{idColumn}` = @targetRecordId";
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;

        var p = command.CreateParameter();
        p.ParameterName = "@targetRecordId";
        p.Value = idValue;
        command.Parameters.Add(p);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return snapshot;
    }

    /// <summary>
    /// Retrieves structural table mappings leveraging an internal request-scoped transaction cache.
    /// </summary>
    private async Task<List<FieldMapper>> GetMappersCachedAsync(
        string tableName,
        DbConnection conn,
        Dictionary<string, List<FieldMapper>> cache)
    {
        if (cache.TryGetValue(tableName, out var list))
        {
            return list;
        }

        var mappers = await _fieldMapperRepository.GetFieldMappersAsync(tableName, conn);
        cache[tableName] = mappers;
        return mappers;
    }

    /// <summary>
    /// Translates special framework evaluation string macros into strongly-typed runtime objects.
    /// </summary>
    private static object? ResolveTokens(object? value, string? defaultValue, string? parentId)
    {
        if (value == null)
        {
            if (defaultValue == "now" || defaultValue == "|Todaydate|") return DateTime.UtcNow;
            if (defaultValue == "|RENGUID|") return Guid.NewGuid().ToString();
            return null;
        }

        string str = value.ToString()!;
        if (str == "now" || str == "|Todaydate|") return DateTime.UtcNow;
        if (str == "|RENGUID|") return parentId ?? Guid.NewGuid().ToString();

        return value;
    }
}