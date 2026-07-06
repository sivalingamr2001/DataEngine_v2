using DataEngine.Core.Interfaces;
using DataEngine.Core.Providers;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using MySqlConnector;

namespace DataEngine.Core.Idempotency;

public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<IdempotencyRepository> _logger;

    public IdempotencyRepository(IDatabaseConnectionFactory connectionFactory, ILogger<IdempotencyRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InsertInProgressAsync(string transactionId, string entityName, string requestHash, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await EnsureTableAsync(connection, transaction, ct);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = GetInsertInProgressSql(connection);
            AddParameter(command, "transactionId", transactionId);
            AddParameter(command, "entityName", entityName);
            AddParameter(command, "requestHash", requestHash);

            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogWarning(ex, "Unable to persist idempotency claim for transaction {TransactionId}.", transactionId);
            throw;
        }
    }

    public async Task<IdempotencyRecord?> GetAsync(string transactionId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = GetSelectSql(connection);
        AddParameter(command, "transactionId", transactionId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new IdempotencyRecord(
            reader.GetString(reader.GetOrdinal("transaction_id")),
            reader.GetString(reader.GetOrdinal("entity_name")),
            reader.GetString(reader.GetOrdinal("request_hash")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.IsDBNull(reader.GetOrdinal("result_json")) ? null : reader.GetString(reader.GetOrdinal("result_json")),
            reader.GetDateTime(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : reader.GetDateTime(reader.GetOrdinal("completed_at")));
    }

    public async Task MarkCompletedAsync(string transactionId, string resultJson, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = GetUpdateCompletedSql(connection);
        AddParameter(command, "transactionId", transactionId);
        AddParameter(command, "resultJson", resultJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(string transactionId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreatePrimaryConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = GetUpdateFailedSql(connection);
        AddParameter(command, "transactionId", transactionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureTableAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (connection is MySqlConnection)
            {
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS de_idempotency_keys (
                        transaction_id VARCHAR(100) NOT NULL PRIMARY KEY,
                        entity_name VARCHAR(128) NOT NULL,
                        request_hash CHAR(64) NOT NULL,
                        status VARCHAR(20) NOT NULL,
                        result_json JSON NULL,
                        created_at DATETIME(6) NOT NULL,
                        completed_at DATETIME(6) NULL,
                        expires_at DATETIME(6) NOT NULL,
                        INDEX ix_de_idempotency_expires (expires_at)
                    );";
            }
            else if (connection is OracleConnection)
            {
                command.CommandText = @"
                    BEGIN
                        EXECUTE IMMEDIATE 'CREATE TABLE de_idempotency_keys (
                            transaction_id VARCHAR2(100) PRIMARY KEY,
                            entity_name VARCHAR2(128) NOT NULL,
                            request_hash CHAR(64) NOT NULL,
                            status VARCHAR2(20) NOT NULL,
                            result_json CLOB NULL,
                            created_at TIMESTAMP(6) NOT NULL,
                            completed_at TIMESTAMP(6) NULL,
                            expires_at TIMESTAMP(6) NOT NULL)';
                    EXCEPTION
                        WHEN OTHERS THEN
                            IF SQLCODE != -955 THEN
                                RAISE;
                            END IF;
                    END;";
            }
            else
            {
                return;
            }

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency table initialization skipped because the target database did not allow it.");
        }
    }

    private string GetInsertInProgressSql(DbConnection connection)
    {
        if (connection is MySqlConnection)
        {
            return @"
                INSERT INTO de_idempotency_keys (transaction_id, entity_name, request_hash, status, created_at, expires_at)
                VALUES (@transactionId, @entityName, @requestHash, 'Processing', UTC_TIMESTAMP(6), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 7 DAY))
                ON DUPLICATE KEY UPDATE transaction_id = transaction_id;";
        }

        return @"
            BEGIN
                INSERT INTO de_idempotency_keys (transaction_id, entity_name, request_hash, status, created_at, expires_at)
                VALUES (:transactionId, :entityName, :requestHash, 'Processing', SYSTIMESTAMP, SYSTIMESTAMP + INTERVAL '7' DAY);
            EXCEPTION
                WHEN DUP_VAL_ON_INDEX THEN
                    NULL;
            END;";
    }

    private static string GetSelectSql(DbConnection connection)
    {
        return connection is OracleConnection
            ? "SELECT transaction_id, entity_name, request_hash, status, result_json, created_at, completed_at FROM de_idempotency_keys WHERE transaction_id = :transactionId"
            : "SELECT transaction_id, entity_name, request_hash, status, result_json, created_at, completed_at FROM de_idempotency_keys WHERE transaction_id = @transactionId";
    }

    private static string GetUpdateCompletedSql(DbConnection connection)
    {
        return connection is OracleConnection
            ? "UPDATE de_idempotency_keys SET status = 'Completed', result_json = :resultJson, completed_at = SYSTIMESTAMP, expires_at = SYSTIMESTAMP + INTERVAL '7' DAY WHERE transaction_id = :transactionId"
            : "UPDATE de_idempotency_keys SET status = 'Completed', result_json = @resultJson, completed_at = UTC_TIMESTAMP(6), expires_at = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 7 DAY) WHERE transaction_id = @transactionId";
    }

    private static string GetUpdateFailedSql(DbConnection connection)
    {
        return connection is OracleConnection
            ? "UPDATE de_idempotency_keys SET status = 'Failed', completed_at = SYSTIMESTAMP WHERE transaction_id = :transactionId"
            : "UPDATE de_idempotency_keys SET status = 'Failed', completed_at = UTC_TIMESTAMP(6) WHERE transaction_id = @transactionId";
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name.StartsWith(":") || name.StartsWith("@") ? name : name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }
}
