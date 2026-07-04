using Newtonsoft.Json;

namespace DataEngine.Core.Domain;

/// <summary>
/// Represents a validation configuration stored in the database
/// </summary>
public class ValidationConfig
{
    public int Id { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string ValidationConfigJson { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

/// <summary>
/// Validation configuration structure (parsed from JSON)
/// </summary>
public class ValidationConfiguration
{
    [JsonProperty("fields")]
    public List<FieldValidation> Fields { get; set; } = new();

    [JsonProperty("businessRules")]
    public List<BusinessRule> BusinessRules { get; set; } = new();
}

/// <summary>
/// Field-level validation rules
/// </summary>
public class FieldValidation
{
    [JsonProperty("fieldName")]
    public string FieldName { get; set; } = string.Empty;

    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }

    [JsonProperty("validation")]
    public List<ValidationRule> Validation { get; set; } = new();
}

/// <summary>
/// Individual validation rule with comprehensive FluentValidation support
/// Supports all common FluentValidation rules with samples
/// </summary>
public class ValidationRule
{
    [JsonProperty("rule")]
    public string Rule { get; set; } = string.Empty;

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("minValue")]
    public string? MinValue { get; set; }

    [JsonProperty("maxValue")]
    public string? MaxValue { get; set; }

    [JsonProperty("pattern")]
    public string? Pattern { get; set; }

    [JsonProperty("allowedValues")]
    public List<string>? AllowedValues { get; set; }

    [JsonProperty("condition")]
    public string? Condition { get; set; }

    [JsonProperty("ruleName")]
    public string? RuleName { get; set; }

    // Additional FluentValidation properties
    [JsonProperty("exactLength")]
    public string? ExactLength { get; set; }

    [JsonProperty("inclusiveBetween")]
    public string? InclusiveBetween { get; set; }

    [JsonProperty("exclusiveBetween")]
    public string? ExclusiveBetween { get; set; }

    [JsonProperty("greaterThan")]
    public string? GreaterThan { get; set; }

    [JsonProperty("greaterThanOrEqual")]
    public string? GreaterThanOrEqual { get; set; }

    [JsonProperty("lessThan")]
    public string? LessThan { get; set; }

    [JsonProperty("lessThanOrEqual")]
    public string? LessThanOrEqual { get; set; }

    [JsonProperty("equal")]
    public string? Equal { get; set; }

    [JsonProperty("notEqual")]
    public string? NotEqual { get; set; }

    [JsonProperty("empty")]
    public bool Empty { get; set; }

    [JsonProperty("notEmpty")]
    public bool NotEmpty { get; set; }

    [JsonProperty("null")]
    public bool Null { get; set; }

    [JsonProperty("notNull")]
    public bool NotNull { get; set; }

    [JsonProperty("matches")]
    public string? Matches { get; set; }

    [JsonProperty("emailAddress")]
    public bool EmailAddress { get; set; }

    [JsonProperty("url")]
    public bool Url { get; set; }

    [JsonProperty("creditCard")]
    public bool CreditCard { get; set; }

    [JsonProperty("regex")]
    public string? Regex { get; set; }

    [JsonProperty("length")]
    public string? Length { get; set; }

    [JsonProperty("scalePrecision")]
    public string? ScalePrecision { get; set; }

    [JsonProperty("custom")]
    public string? Custom { get; set; }

    [JsonProperty("mustHaveAtLeastOneOf")]
    public List<string>? MustHaveAtLeastOneOf { get; set; }

    [JsonProperty("mustHaveExactlyOneOf")]
    public List<string>? MustHaveExactlyOneOf { get; set; }

    [JsonProperty("dependentRules")]
    public List<string>? DependentRules { get; set; }

    [JsonProperty("when")]
    public string? When { get; set; }

    [JsonProperty("unless")]
    public string? Unless { get; set; }

    [JsonProperty("transform")]
    public string? Transform { get; set; }

    [JsonProperty("severity")]
    public string? Severity { get; set; } // Error, Warning, Info

    [JsonProperty("asyncValidator")]
    public bool AsyncValidator { get; set; }

    [JsonProperty("customMessage")]
    public string? CustomMessage { get; set; }
}

/// <summary>
/// Business-level validation rule
/// </summary>
public class BusinessRule
{
    [JsonProperty("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("ruleType")]
    public string RuleType { get; set; } = string.Empty;

    [JsonProperty("expression")]
    public string? Expression { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of validation operation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationError> Errors { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Individual validation error
/// </summary>
public class ValidationError
{
    public string FieldName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
}