using DataEngine.ReaderService.Domain;
using MySqlConnector;
using System.Data.Common;

namespace DataEngine.Core.Providers;

public sealed class MySqlProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.MySQL;

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) => $"`{identifier}`";

    public string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName)
    {
        var trimmed = baseSelect.Trim();
        return trimmed + $" LIMIT {limitParameterName} OFFSET {offsetParameterName}";
    }

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn)
    {
        return $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}); SELECT LAST_INSERT_ID();";
    }

    public string CurrentTimestampExpression => "NOW()";

    public string BuildDateAddDaysExpression(string timestampExpression, int days)
    {
        return $"DATE_ADD({timestampExpression}, INTERVAL {days} DAY)";
    }

    public string ParameterPrefix => "@";

    public bool IsTransient(Exception ex)
    {
        return ex is MySqlException mysqlEx && (mysqlEx.Number == 1213 || mysqlEx.Number == 1205 || mysqlEx.Number == 2013 || mysqlEx.Number == 2006 || mysqlEx.Number == 1042);
    }
}
