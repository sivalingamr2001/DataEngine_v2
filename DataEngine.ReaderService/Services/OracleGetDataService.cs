using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using System.Data.Common;

namespace DataEngine.ReaderService.Services;

public sealed class OracleGetDataService(DatabaseConnectionFactory connectionFactory) : IGetDataService
{
    private readonly DatabaseConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
    {
        // Pull from connection pool
        await using DbConnection connection = await _connectionFactory.CreateReadReplicaConnectionAsync(ct);

        throw new NotImplementedException("Oracle data reading execution logic is pending.");
    }
}
