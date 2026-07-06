using System.Data.Common;
using System.Collections.Generic;
using DataEngine.ReaderService.Domain;

namespace DataEngine.Core.Providers;

public interface IDbProviderStrategy
{
    DatabaseProvider Provider { get; }

    DbConnection CreateConnection(string connectionString);

    string QuoteIdentifier(string identifier);

    string BuildPagedQuery(string baseSelect, string limitParameterName, string offsetParameterName);

    string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn);

    string CurrentTimestampExpression { get; }

    string BuildDateAddDaysExpression(string timestampExpression, int days);

    string ParameterPrefix { get; }

    bool IsTransient(Exception ex);

    string NormalizeParameterName(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return parameterName!;
        }

        if (parameterName.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        {
            return parameterName;
        }

        return ParameterPrefix + parameterName.TrimStart('@', ':');
    }
}
