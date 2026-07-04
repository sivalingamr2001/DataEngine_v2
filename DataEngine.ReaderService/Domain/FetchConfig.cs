using DataEngine.ReaderService.Enums;
using System.Text.Json;

namespace DataEngine.ReaderService.Domain;

/// <summary>
/// Immutable configuration for query execution configurations.
/// </summary>
public record FetchConfig
{
    /// <summary>
    /// Looks up a stored, pre-validated query definition by key.
    /// </summary>
    public int? QueryNumber { get; init; }

    /// <summary>
    /// Looks up a stored query definition by its unique text key string.
    /// </summary>
    public string? QueryKey { get; init; }

    /// <summary>
    /// Raw SQL text executed only when allowed and validated.
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// Activates raw SQL execution pathways.
    /// </summary>
    public bool EnableDirectQueryExecution { get; init; } = false;

    /// <summary>
    /// Named parameters wrapped inside a structured JSON document payload.
    /// </summary>
    public JsonDocument? InputParameters { get; init; }

    /// <summary>
    /// Total records to extract per request processing execution.
    /// </summary>
    public int Count { get; init; } = 10;

    /// <summary>
    /// Page number position offset matching target paginated indices.
    /// </summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>
    /// Controls sorting operations across execution runtimes.
    /// </summary>
    public bool EnableServerSideSorting { get; init; } = false;

    /// <summary>
    /// Column fields targeted for sorting operations.
    /// </summary>
    public string? SortField { get; init; }

    /// <summary>
    /// Direction constraints applied over sorted columns.
    /// </summary>
    public SortDirection SortDirection { get; init; } = SortDirection.Asc;

    /// <summary>
    /// Controls server-side filters application over query scopes.
    /// </summary>
    public bool EnableServerSideFiltering { get; init; } = false;

    /// <summary>
    /// Collection of structured condition items used to restrict query outputs.
    /// </summary>
    public IReadOnlyList<FilterCondition> FilterConditions { get; init; } = [];

    /// <summary>
    /// Structured evaluation strings passed down into conditional blocks.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// Timezone identifier offset mapping configurations.
    /// </summary>
    public string? FetchTimezone { get; init; }
}

/// <summary>
/// Structured filter evaluation schema mapped to database fields.
/// </summary>
public sealed record FilterCondition
{
    /// <summary>
    /// Target database table column name indicator.
    /// </summary>
    public string Column { get; init; } = string.Empty;

    /// <summary>
    /// Domain schema model field name descriptor.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Evaluation operation used to weigh parameter data boundaries.
    /// </summary>
    public string? Operator { get; init; }

    /// <summary>
    /// Object value passed downstream inside targeted safe parameter structures.
    /// </summary>
    public object? Value { get; init; }
}
