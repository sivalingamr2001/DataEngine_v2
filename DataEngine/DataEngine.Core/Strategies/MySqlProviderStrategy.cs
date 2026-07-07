using System;
using System.Data;
using System.Data.Common;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using MySqlConnector;

namespace DataEngine.Core.Strategies;

/// <summary>
/// High-efficiency, dialect-specific execution strategy mapping for MySQL/MariaDB database backends.
/// </summary>
public sealed class MySqlProviderStrategy : IDbProviderStrategy
{
    /// <inheritdoc />
    public DatabaseProvider Provider => DatabaseProvider.MySQL;

    /// <inheritdoc />
    public string ParameterPrefix => "@";

    /// <inheritdoc />
    public string CurrentTimestampExpression => "UTC_TIMESTAMP()";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL does not natively support inline RETURNING or OUTPUT syntax blocks in standard INSERT profiles.
    /// Requires secondary pipeline resolution via SELECT LAST_INSERT_ID().
    /// </remarks>
    public bool SupportsInlineOutputClause => false;

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return string.Empty;
        return $"`{identifier.Replace("`", "``")}`";
    }

    /// <inheritdoc />
    public string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSelect);

        // Appends normalized positional parameters using your strategy prefix
        string cleanLimit = NormalizeParameterName(limitParameterName);
        string cleanOffset = NormalizeParameterName(offsetParameterName);

        return $"{baseSelect.Trim().TrimEnd(';')} LIMIT {cleanLimit} OFFSET {cleanOffset}";
    }

    /// <inheritdoc />
    public string BuildInsertReturningKey(string baseInsertSql, string idColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseInsertSql);

        // MySQL handles data insertion synchronously inside a singular connection command statement loop.
        // Simply return the pristine statement; the engine will append identity recovery separately.
        return baseInsertSql.Trim().TrimEnd(';');
    }

    /// <inheritdoc />
    public string BuildLastInsertIdQuery() => "SELECT LAST_INSERT_ID();";

    /// <inheritdoc />
    public string BuildDateAddDaysExpression(string timestampExpression, int days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampExpression);
        return $"DATE_ADD({timestampExpression}, INTERVAL {days} DAY)";
    }

    /// <inheritdoc />
    public bool IsTransient(Exception ex) => ex is MySqlException mysqlEx && mysqlEx.IsTransient;

    /// <inheritdoc />
    public DbType GetDbType(string dataTypeName)
    {
        if (string.IsNullOrWhiteSpace(dataTypeName)) return DbType.Object;

        return dataTypeName.ToUpperInvariant() switch
        {
            "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" => DbType.Int64,
            "DECIMAL" or "NUMERIC" or "FLOAT" or "DOUBLE" => DbType.Decimal,
            "VARCHAR" or "TEXT" or "CHAR" or "LONGTEXT" or "JSON" => DbType.String,
            "DATETIME" or "TIMESTAMP" or "DATE" => DbType.DateTime,
            "BOOL" or "BOOLEAN" => DbType.Boolean,
            "BLOB" or "LONGBLOB" or "BINARY" or "VARBINARY" => DbType.Binary,
            "GUID" or "UUID" => DbType.Guid,
            _ => DbType.Object
        };
    }

    /// <inheritdoc />
    public string NormalizeParameterName(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return parameterName ?? string.Empty;

        if (parameterName.StartsWith(ParameterPrefix, StringComparison.Ordinal))
            return parameterName;

        ReadOnlySpan<char> trimmed = parameterName.AsSpan();
        while (trimmed.Length > 0 && (trimmed[0] == '@' || trimmed[0] == ':'))
        {
            trimmed = trimmed[1..];
        }

        return ParameterPrefix + trimmed.ToString();
    }
}
