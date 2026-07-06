using DataEngine.ReaderService.Domain;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;

namespace DataEngine.Core.Providers;

public sealed class OracleProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.Oracle;

    public DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);

    public string QuoteIdentifier(string identifier) => $"\"{identifier.ToUpperInvariant()}\"";

    public string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName)
    {
        return baseSelect.Trim() + $" OFFSET {offsetParameterName} ROWS FETCH NEXT {limitParameterName} ROWS ONLY";
    }

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn)
    {
        if (string.IsNullOrWhiteSpace(identityColumn))
        {
            return $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)})";
        }

        return $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}) RETURNING {QuoteIdentifier(identityColumn)} INTO :generatedId";
    }

    public string CurrentTimestampExpression => "SYSTIMESTAMP";

    public string BuildDateAddDaysExpression(string timestampExpression, int days)
    {
        return $"{timestampExpression} + INTERVAL '{days}' DAY";
    }

    public string ParameterPrefix => ":";

    public bool IsTransient(Exception ex)
    {
        return ex is OracleException oracleEx && (oracleEx.Number == 12545 || oracleEx.Number == 12541 || oracleEx.Number == 3113 || oracleEx.Number == 60 || oracleEx.Number == 54);
    }
}
