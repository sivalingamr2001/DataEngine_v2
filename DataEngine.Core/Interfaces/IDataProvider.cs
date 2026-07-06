using System.Data;

namespace DataEngine.Core.Interfaces;

public interface IDataProvider
{
    Task<IDbConnection> GetConnectionAsync();
    Task<IDbTransaction> BeginTransactionAsync(IDbConnection connection);
    Task<object?> ExecuteScalarAsync(string query, Dictionary<string, object>? parameters = null, IDbTransaction? transaction = null);
}
