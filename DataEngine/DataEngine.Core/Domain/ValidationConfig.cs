using System.Text.Json.Serialization;

namespace DataEngine.Core.Domain;

// CHANGED: All to records for immutability and thread safety
public sealed record ValidationConfig
{
    public int Id { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public string ValidationConfigJson { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    public DateTime CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

public sealed record ValidationConfiguration
{
    [JsonPropertyName("fields")]
    public IReadOnlyList<FieldValidation> Fields { get; init; } = [];

    [JsonPropertyName("businessRules")]
    public IReadOnlyList<BusinessRule> BusinessRules { get; init; } = [];
}

public sealed record FieldValidation
{
    [JsonPropertyName("fieldName")]
    public string FieldName { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("validation")]
    public IReadOnlyList<ValidationRule> Validation { get; init; } = [];
}

public sealed record ValidationRule
{
    [JsonPropertyName("rule")]
    public string Rule { get; init; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("minValue")]
    public string? MinValue { get; init; }

    [JsonPropertyName("maxValue")]
    public string? MaxValue { get; init; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    [JsonPropertyName("allowedValues")]
    public IReadOnlyList<string>? AllowedValues { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; } = "Error";

    [JsonPropertyName("when")]
    public string? When { get; init; }
}

public sealed record BusinessRule
{
    [JsonPropertyName("ruleName")]
    public string RuleName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("ruleType")]
    public string RuleType { get; init; } = string.Empty;

    [JsonPropertyName("expression")]
    public string? Expression { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

public sealed record ValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}