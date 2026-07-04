using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;

namespace DataEngine.ReaderService.Repositories;

/// <summary>
/// Access provider reading engine layout parameters from schema configurations.
/// </summary>
public class QueryRepository : IQueryRepository
{
    private readonly ILogger<QueryRepository> _logger;

    public QueryRepository(ILogger<QueryRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<QueryDefinition?> GetQueryDefinitionAsync(int? id, string? definitionKey, IDbConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();

            if (id.HasValue)
            {
                command.CommandText = @"
                    SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by 
                    FROM de_query_definitions 
                    WHERE id = @id AND is_active = 1";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = id.Value;
                command.Parameters.Add(parameter);
            }
            else if (!string.IsNullOrWhiteSpace(definitionKey))
            {
                command.CommandText = @"
                    SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by 
                    FROM de_query_definitions 
                    WHERE definition_key = @definitionKey AND is_active = 1";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@definitionKey";
                parameter.Value = definitionKey;
                command.Parameters.Add(parameter);
            }
            else
            {
                return null;
            }

            using var reader = await ((DbCommand)command).ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return MapRow(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving query definition for Id: {Id} or Key: {Key}", id, definitionKey);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<QueryDefinition>> GetAllQueryDefinitionsAsync(IDbConnection connection)
    {
        var queries = new List<QueryDefinition>();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, definition_key, table_name, description, query_text, is_active, created_at, updated_at, created_by, updated_by 
                FROM de_query_definitions 
                WHERE is_active = 1 
                ORDER BY id";

            using var reader = await ((DbCommand)command).ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                queries.Add(MapRow(reader));
            }

            return queries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all active query definitions from database cluster.");
            throw;
        }
    }

    private static QueryDefinition MapRow(DbDataReader reader)
    {
        int idIdx = reader.GetOrdinal("id");
        int keyIdx = reader.GetOrdinal("definition_key");
        int tableIdx = reader.GetOrdinal("table_name");
        int descIdx = reader.GetOrdinal("description");
        int textIdx = reader.GetOrdinal("query_text");
        int activeIdx = reader.GetOrdinal("is_active");
        int createdDateIdx = reader.GetOrdinal("created_at");
        int updatedDateIdx = reader.GetOrdinal("updated_at");
        int createdByIdx = reader.GetOrdinal("created_by");
        int updatedByIdx = reader.GetOrdinal("updated_by");

        return new QueryDefinition
        {
            Id = reader.GetInt32(idIdx),
            DefinitionKey = reader.GetString(keyIdx),
            TableName = reader.GetString(tableIdx),
            Description = reader.IsDBNull(descIdx) ? null : reader.GetString(descIdx),
            QueryText = reader.GetString(textIdx),
            IsActive = reader.GetBoolean(activeIdx),
            CreatedAt = reader.GetDateTime(createdDateIdx),
            UpdatedAt = reader.IsDBNull(updatedDateIdx) ? null : reader.GetDateTime(updatedDateIdx),
            CreatedBy = reader.IsDBNull(createdByIdx) ? "System" : reader.GetString(createdByIdx),
            UpdatedBy = reader.IsDBNull(updatedByIdx) ? null : reader.GetString(updatedByIdx)
        };
    }
}
