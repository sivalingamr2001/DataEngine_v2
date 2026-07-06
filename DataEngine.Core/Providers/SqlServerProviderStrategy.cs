using DataEngine.ReaderService.Domain;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DataEngine.Core.Providers;

public sealed class SqlServerProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) => $"[{identifier}]";

    public string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName)
    {
        var trimmed = baseSelect.Trim();
        if (!trimmed.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += " ORDER BY (SELECT 1)";
        }

        return $"{trimmed} OFFSET {offsetParameterName} ROWS FETCH NEXT {limitParameterName} ROWS ONLY";
    }

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn)
    {
        if (string.IsNullOrWhiteSpace(identityColumn))
        {
            return $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)})";
        }

        return $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) OUTPUT INSERTED.{QuoteIdentifier(identityColumn)} VALUES ({string.Join(", ", paramNames)})";
    }

    public string CurrentTimestampExpression => "SYSUTCDATETIME()";

    public string BuildDateAddDaysExpression(string timestampExpression, int days)
    {
        return $"DATEADD(DAY, {days}, {timestampExpression})";
    }

    public string ParameterPrefix => "@";

    public bool IsTransient(Exception ex)
    {
        return ex is SqlException sqlEx && (sqlEx.Number == 1205 || sqlEx.Number == -2 || sqlEx.Number == 4060 || sqlEx.Number == 40613 || sqlEx.Number == 40197);
    }
}
