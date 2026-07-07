using System.Data;
using Dapper;
using DataEngine.Core.Domain;
using DataEngine.Core.Exceptions;
using DataEngine.Core.Interfaces;

namespace DataEngine.Core.Repositories;

public sealed class QueryRepository : IQueryRepository
{
    public async Task<QueryDefinition?> GetQueryDefinitionAsync(
        int? id,
        string? definitionKey,
        IDbConnection connection,
        CancellationToken cancellationToken = default)
    {
        string sql;
        var parameters = new DynamicParameters();

        if (id.HasValue)
        {
            sql = """
                SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by 
                FROM de_query_definitions 
                WHERE id = @id AND is_active = 1
                """;
            parameters.Add("id", id.Value);
        }
        else if (!string.IsNullOrWhiteSpace(definitionKey))
        {
            sql = """
                SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by 
                FROM de_query_definitions 
                WHERE definition_key = @definitionKey AND is_active = 1
                """;
            parameters.Add("definitionKey", definitionKey);
        }
        else
        {
            return null;
        }

        try
        {
            return await connection.QueryFirstOrDefaultAsync<QueryDefinition>(
                new CommandDefinition(sql, parameters, transaction: null, cancellationToken: cancellationToken));
        }
        catch (Exception ex) when (IsMissingTableException(ex))
        {
            throw new ConfigurationException(
                "Query definitions table 'de_query_definitions' is missing. Run database migrations.", ex);
        }
    }

    public async Task<IReadOnlyList<QueryDefinition>> GetAllQueryDefinitionsAsync(
        IDbConnection connection,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by
            FROM de_query_definitions
            WHERE is_active = 1
            """;

        try
        {
            var result = await connection.QueryAsync<QueryDefinition>(
                new CommandDefinition(sql, transaction: null, cancellationToken: cancellationToken));
            return result.ToList();
        }
        catch (Exception ex) when (IsMissingTableException(ex))
        {
            throw new ConfigurationException(
                "Query definitions table 'de_query_definitions' is missing. Run database migrations.", ex);
        }
    }

    private static bool IsMissingTableException(Exception ex) =>
        ex.Message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("ORA-00942", StringComparison.OrdinalIgnoreCase);
}
