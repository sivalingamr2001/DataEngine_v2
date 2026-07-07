using System;
using System.Data;
using System.Data.Common;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using Oracle.ManagedDataAccess.Client;

namespace DataEngine.Core.Strategies;

/// <summary>
/// High-efficiency, dialect-specific execution strategy mapping for Oracle Database backends.
/// </summary>
public sealed class OracleProviderStrategy : IDbProviderStrategy
{
    /// <inheritdoc />
    public DatabaseProvider Provider => DatabaseProvider.Oracle;

    /// <inheritdoc />
    public string ParameterPrefix => ":";

    /// <inheritdoc />
    public string CurrentTimestampExpression => "SYSTIMESTAMP AT TIME ZONE 'UTC'";

    /// <inheritdoc />
    /// <remarks>
    /// Oracle natively supports inline data retrieval via the RETURNING INTO clause.
    /// This property should be set to true if you are handling returning variables via Dapper Parameters.
    /// NOTE: If your TransactionEngine execution paths depend on executing a secondary query statement, 
    /// set this to false and throw inside BuildLastInsertIdQuery. 
    /// For this configuration, we set it to true to signal inline output support.
    /// </remarks>
    public bool SupportsInlineOutputClause => true;

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return string.Empty;
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    /// <inheritdoc />
    public string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSelect);

        string cleanLimit = NormalizeParameterName(limitParameterName);
        string cleanOffset = NormalizeParameterName(offsetParameterName);

        // Modern Oracle (12c and above) natively supports standard ANSI pagination syntax 
        // which avoids brittle nested ROWNUM sub-query hacks:
        return $"{baseSelect.Trim().TrimEnd(';')} OFFSET {cleanOffset} ROWS FETCH NEXT {cleanLimit} ROWS ONLY";
    }

    /// <inheritdoc />
    public string BuildInsertReturningKey(string baseInsertSql, string idColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseInsertSql);

        if (string.IsNullOrWhiteSpace(idColumn)) return baseInsertSql;

        // Dynamic interpolation appends Oracle's specific RETURNING clause to the base INSERT sql pipeline string
        string safeIdColumn = QuoteIdentifier(idColumn);
        string paramToken = NormalizeParameterName("RETURN_ID");

        return $"{baseInsertSql.Trim().TrimEnd(';')} RETURNING {safeIdColumn} INTO {paramToken}";
    }

    /// <inheritdoc />
    public string BuildLastInsertIdQuery()
    {
        // Because Oracle utilizes isolated sequences or identity triggers mapped via RETURNING INTO parameters,
        // it cannot execute global scalar hooks like MySQL's LAST_INSERT_ID().
        throw new NotSupportedException("Oracle requires an inline RETURNING clause parameter binding infrastructure. Explicit scalar lookup loops are not supported.");
    }

    /// <inheritdoc />
    public string BuildDateAddDaysExpression(string timestampExpression, int days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampExpression);
        return $"{timestampExpression} + INTERVAL '{days}' DAY";
    }

    /// <inheritdoc />
    public bool IsTransient(Exception ex) => ex is OracleException oraEx &&
        (oraEx.Number is 12170 or 12541 or 12543 or 03113 or 03114 or 03135);

    /// <inheritdoc />
    public DbType GetDbType(string dataTypeName)
    {
        if (string.IsNullOrWhiteSpace(dataTypeName)) return DbType.Object;

        return dataTypeName.ToUpperInvariant() switch
        {
            "NUMBER" or "INTEGER" or "INT" => DbType.Int64,
            "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" => DbType.Decimal,
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" or "CLOB" or "NCLOB" or "CHAR" => DbType.String,
            "DATE" or "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => DbType.DateTime,
            "BLOB" or "RAW" or "LONG RAW" => DbType.Binary,
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

        // FIXED: Use index 0 to compare the first character of the span to the char literal
        while (trimmed.Length > 0 && (trimmed[0] == '@' || trimmed[0] == ':'))
        {
            trimmed = trimmed[1..];
        }

        return ParameterPrefix + trimmed.ToString();
    }
}
