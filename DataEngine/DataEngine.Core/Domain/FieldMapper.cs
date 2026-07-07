namespace DataEngine.Core.Domain;

/// <summary>Entity column mapping metadata.</summary>
public sealed record FieldMapper
{
    public required string FieldName { get; init; }

    public required string ColumnName { get; init; }

    public required string DataType { get; init; }

    public string? DefaultValue { get; init; }

    public string? Properties { get; init; }

    public bool IsActive { get; init; } = true;

    public bool AllowUpdate { get; init; } = true;
}