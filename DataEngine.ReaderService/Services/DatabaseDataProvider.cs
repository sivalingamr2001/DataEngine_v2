using DataEngine.Core.Interfaces;
using DataEngine.Core.Providers;
using DataEngine.ReaderService.Domain;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;

namespace DataEngine.ReaderService.Services;

public sealed class DatabaseDataProvider : IDataProvider
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IDbProviderStrategy _providerStrategy;
    private readonly DatabaseConfig _config;
    private readonly ILogger<DatabaseDataProvider> _logger;

    public DatabaseDataProvider(
        DatabaseConnectionFactory connectionFactory,
        DatabaseConfig config,
        IDbProviderStrategy providerStrategy,
        ILogger<DatabaseDataProvider> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providerStrategy = providerStrategy ?? throw new ArgumentNullException(nameof(providerStrategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IDbConnection> GetConnectionAsync()
    {
        return await _connectionFactory.CreatePrimaryConnectionAsync();
    }

    public async Task<IDbTransaction> BeginTransactionAsync(IDbConnection connection)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));

        if (connection is DbConnection dbConnection)
        {
            return await dbConnection.BeginTransactionAsync();
        }

        throw new InvalidOperationException("The provided connection is not a DbConnection and cannot start an asynchronous transaction.");
    }

    public async Task<object?> ExecuteScalarAsync(string query, Dictionary<string, object>? parameters = null, IDbTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query text must be provided.", nameof(query));

        if (transaction != null)
        {
            if (transaction.Connection is not DbConnection transactionConnection)
                throw new InvalidOperationException("Unsupported transaction connection type.");

            if (transaction is not DbTransaction dbTransaction)
                throw new InvalidOperationException("Unsupported transaction type for database command execution.");

            await using var txCommand = transactionConnection.CreateCommand();
            txCommand.Transaction = dbTransaction;
            txCommand.CommandText = query;
            ApplyParameters(txCommand, parameters);
            return await txCommand.ExecuteScalarAsync();
        }

        await using var connection = (DbConnection)await GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        ApplyParameters(command, parameters);
        return await command.ExecuteScalarAsync();
    }

    private void ApplyParameters(DbCommand command, Dictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return;

        foreach (var parameter in parameters)
        {
            var normalizedName = _providerStrategy.NormalizeParameterName(parameter.Key.TrimStart('@', ':'));
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = normalizedName;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }
    }
}
