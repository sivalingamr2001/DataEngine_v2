using System.Data.Common;

namespace DataEngine.Core.Interfaces;

public interface IDatabaseConnectionFactory
{
    Task<DbConnection> CreatePrimaryConnectionAsync(CancellationToken ct = default);
    Task<DbConnection> CreateReadReplicaConnectionAsync(CancellationToken ct = default);
}
