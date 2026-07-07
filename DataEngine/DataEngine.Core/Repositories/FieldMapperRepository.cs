using System.Data;
using Dapper;
using DataEngine.Core.Domain;
using DataEngine.Core.Interfaces;

namespace DataEngine.Core.Repositories;

public sealed class FieldMapperRepository : IFieldMapperRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ISqlGuardian _sqlGuardian;

    public FieldMapperRepository(IDbConnectionFactory connectionFactory, ISqlGuardian sqlGuardian)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _sqlGuardian = sqlGuardian ?? throw new ArgumentNullException(nameof(sqlGuardian));
    }

    public async Task<IReadOnlyList<FieldMapper>> GetFieldMappersAsync(
        string tableName,
        IDbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var options = _connectionFactory.GetCurrentOptions();
        var strategy = _connectionFactory.GetCurrentStrategy();
        string? sql = options.FieldMappersQuery;

        if (string.IsNullOrWhiteSpace(sql))
        {
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersTableName, "FieldMappersTableName");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnFieldName, "FieldMappersColumnFieldName");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnColumnName, "FieldMappersColumnColumnName");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnDataType, "FieldMappersColumnDataType");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnDefaultValue, "FieldMappersColumnDefaultValue");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnProperties, "FieldMappersColumnProperties");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnIsActive, "FieldMappersColumnIsActive");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnAllowUpdate, "FieldMappersColumnAllowUpdate");
            _sqlGuardian.ValidateConfigIdentifier(options.FieldMappersColumnTableName, "FieldMappersColumnTableName");

            sql = $"""
                SELECT {strategy.QuoteIdentifier(options.FieldMappersColumnFieldName!)} AS FieldName,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnColumnName!)} AS ColumnName,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnDataType!)} AS DataType,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnDefaultValue!)} AS DefaultValue,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnProperties!)} AS Properties,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnIsActive!)} AS IsActive,
                       {strategy.QuoteIdentifier(options.FieldMappersColumnAllowUpdate!)} AS AllowUpdate
                FROM {strategy.QuoteIdentifier(options.FieldMappersTableName!)}
                WHERE {strategy.QuoteIdentifier(options.FieldMappersColumnTableName!)} = @TableName
                  AND {strategy.QuoteIdentifier(options.FieldMappersColumnIsActive!)} = 1
                """;
        }

        var result = await connection.QueryAsync<FieldMapper>(
            new CommandDefinition(sql, new { TableName = tableName }, cancellationToken: cancellationToken));

        return result.ToList();
    }
}
