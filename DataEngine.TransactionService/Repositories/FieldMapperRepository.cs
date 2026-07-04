using DataEngine.TransactionService.Domain;
using DataEngine.TransactionService.Interfaces;
using System.Data;
using System.Data.Common;

namespace DataEngine.TransactionService.Repositories;

/// <summary>
/// Database operations layer extracting layout properties for dynamic query formatting.
/// </summary>
public sealed class FieldMapperRepository : IFieldMapperRepository
{
    /// <inheritdoc />
    public async Task<List<FieldMapper>> GetFieldMappersAsync(string tableName, IDbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT field_name, column_name, data_type, default_value, properties, is_active, allow_update 
            FROM de_field_mappers 
            WHERE table_name = @tableName AND is_active = 1";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var mappers = new List<FieldMapper>();
        using var reader = await ((DbCommand)command).ExecuteReaderAsync();

        int fieldIdx = reader.GetOrdinal("field_name");
        int columnIdx = reader.GetOrdinal("column_name");
        int typeIdx = reader.GetOrdinal("data_type");
        int defaultIdx = reader.GetOrdinal("default_value");
        int propsIdx = reader.GetOrdinal("properties");
        int activeIdx = reader.GetOrdinal("is_active");
        int updateIdx = reader.GetOrdinal("allow_update");

        while (await reader.ReadAsync())
        {
            mappers.Add(new FieldMapper
            {
                FieldName = reader.GetString(fieldIdx),
                ColumnName = reader.GetString(columnIdx),
                DataType = reader.GetString(typeIdx),
                DefaultValue = reader.IsDBNull(defaultIdx) ? null : reader.GetString(defaultIdx),
                Properties = reader.IsDBNull(propsIdx) ? null : reader.GetString(propsIdx),
                IsActive = reader.GetBoolean(activeIdx),
                AllowUpdate = reader.GetBoolean(updateIdx)
            });
        }

        return mappers;
    }
}
