using DataEngine.Core.Domain;

namespace DataEngine.Core.Configuration;

/// <summary>
/// In-memory validation rule configuration keyed by entity/table name.
/// </summary>
public sealed class ValidationOptions
{
    /// <summary>
    /// Entity name (case-insensitive) → validation configuration.
    /// </summary>
    public Dictionary<string, ValidationConfiguration> Entities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional table name for loading validation rules from the database.
    /// </summary>
    public string? ValidationConfigTableName { get; set; } = "de_validation_config";
}
