using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using DataEngine.Core.Enums;

namespace DataEngine.Core.Interfaces;

public interface IDbProviderStrategy
{
    DatabaseProvider Provider { get; }

    DbConnection CreateConnection(string connectionString);

    string QuoteIdentifier(string identifier);

    string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName);

    /// <summary>
    /// FIXED: Realigned signature to accept the base pre-built statement to match your TransactionEngine loop.
    /// </summary>
    string BuildInsertReturningKey(string baseInsertSql, string idColumn);

    /// <summary>
    /// FIXED: Renamed to match the exact call inside your ProcessInsertAsync handler loop.
    /// </summary>
    string BuildLastInsertIdQuery();

    string CurrentTimestampExpression { get; }

    string BuildDateAddDaysExpression(string timestampExpression, int days);

    string ParameterPrefix { get; }

    bool IsTransient(Exception ex);

    /// <summary>
    /// Flags if this database engine handles identity extraction inside the insert pipeline.
    /// </summary>
    bool SupportsInlineOutputClause { get; }

    /// <summary>
    /// Provider-specific parameter type mapping for Dapper.
    /// </summary>
    DbType GetDbType(string dataTypeName);

    /// <summary>
    /// Optimized variant normalizing parameter targeting structures without heap allocations.
    /// </summary>
    string NormalizeParameterName(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return parameterName ?? string.Empty;

        // Uses high-performance character spans to check prefix footprints 
        if (parameterName.StartsWith(ParameterPrefix, StringComparison.Ordinal))
            return parameterName;

        // Strips common cross-database identifier prefix markers safely
        ReadOnlySpan<char> trimmed = parameterName.AsSpan();
        while (trimmed.Length > 0 && (trimmed[0] == '@' || trimmed[0] == ':'))
        {
            trimmed = trimmed[1..];
        }

        return ParameterPrefix + trimmed.ToString();
    }
}
