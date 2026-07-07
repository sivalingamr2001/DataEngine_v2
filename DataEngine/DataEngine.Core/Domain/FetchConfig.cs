using DataEngine.Core.Enums;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataEngine.Core.Domain;

/// <summary>
/// Immutable configuration for query execution.
/// </summary>
public sealed record FetchConfig
{
    public int? QueryNumber { get; init; }

    public string? QueryKey { get; init; }

    public string? QueryText { get; init; }

    public bool EnableDirectQueryExecution { get; init; }

    [JsonPropertyName("inputParameters")]
    public Dictionary<string, object?> Parameters { get; init; } = [];

    [JsonIgnore]
    public Dictionary<string, object?> EffectiveParameters => Parameters;

    public int Count { get; init; } = 10;

    public int PageNumber { get; init; } = 1;

    public bool EnableServerSideSorting { get; init; }

    public string? SortField { get; init; }

    public SortDirection SortDirection { get; init; } = SortDirection.Asc;

    public bool EnableServerSideFiltering { get; init; }

    public IReadOnlyList<FilterCondition> FilterConditions { get; init; } = [];

    public string? SearchText { get; init; }

    /// <summary>
    /// Optional per-request search column override. Falls back to connection DefaultSearchColumn.
    /// </summary>
    public string? SearchColumn { get; init; }

    /// <summary>
    /// When false, skips the COUNT query for better performance. Defaults to true.
    /// </summary>
    public bool IncludeTotalCount { get; init; } = true;

    public string? FetchTimezone { get; init; }

    // Validation
    internal bool IsValid => QueryNumber.HasValue
        || !string.IsNullOrWhiteSpace(QueryKey)
        || !string.IsNullOrWhiteSpace(QueryText);
}

public sealed record FilterCondition
{
    public required string Column { get; init; }

    public string? Field { get; init; }

    public FilterOperator Operator { get; init; } = FilterOperator.Equals;

    // CHANGED: object? -> JsonElement to avoid boxing, then convert at execution time
    public JsonElement Value { get; init; }
}