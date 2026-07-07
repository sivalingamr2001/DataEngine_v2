using System.ComponentModel.DataAnnotations;
using DataEngine.Core.Enums;

namespace DataEngine.Core.Configuration;

public sealed record DatabaseOptions
{
    [Required]
    public required string ConnectionString { get; init; }

    [Required]
    public DatabaseProvider Provider { get; init; }

    public string DefaultTimezone { get; init; } = "UTC";

    public int MaxPageSize { get; init; } = 1000;

    public bool EnableDirectQueryExecution { get; init; } = false;

    public int MaxDirectQueryLength { get; init; } = 5000;

    public int MaxRetryCount { get; init; } = 3;

    public int RetryDelayMs { get; init; } = 200;

    public int MaxJoinCount { get; init; } = 5;

    public int MaxSubqueryCount { get; init; } = 3;

    public int MaxUnionCount { get; init; } = 2;

    public string? Name { get; init; }

    public bool IsDefault { get; init; } = false;

    public string FieldMappersTableName { get; init; } = "FieldMappers";
    public string FieldMappersColumnFieldName { get; init; } = "FieldName";
    public string FieldMappersColumnColumnName { get; init; } = "ColumnName";
    public string FieldMappersColumnDataType { get; init; } = "DataType";
    public string FieldMappersColumnDefaultValue { get; init; } = "DefaultValue";
    public string FieldMappersColumnProperties { get; init; } = "Properties";
    public string FieldMappersColumnIsActive { get; init; } = "IsActive";
    public string FieldMappersColumnAllowUpdate { get; init; } = "AllowUpdate";
    public string FieldMappersColumnTableName { get; init; } = "TableName";
    public string? FieldMappersQuery { get; init; }

    /// <summary>
    /// Optional default column used for global text search. When null, search is disabled.
    /// </summary>
    public string? DefaultSearchColumn { get; init; }

    /// <summary>
    /// Per-query search column overrides keyed by query key (case-insensitive).
    /// </summary>
    public Dictionary<string, string> SearchColumnOverrides { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Command timeout in seconds for read queries. 0 = provider default.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; }
}

public sealed class DataEngineOptions
{
    [Required, MinLength(1)]
    public List<DatabaseOptions> Connections { get; set; } = [];

    public SecurityOptions Security { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public ValidationOptions Validation { get; set; } = new();
}