using DataEngine.ReaderService.Enums;
using System.Text.Json;

namespace DataEngine.ReaderService.Domain;

/// <summary>
/// Immutable configuration for <see cref="IFetchQueryEngine.ExecuteQueryAsync"/>.
/// All properties are init-only to prevent post-construction mutation.
/// </summary>
public record FetchConfig
{
    // ── Query source ──────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a stored, pre-validated query definition by key.
    /// Preferred over <see cref="QueryText"/> for all production usage.
    /// </summary>
    public int? QueryNumber { get; init; }

    /// <summary>
    /// Raw SQL — only executed when <see cref="EnableDirectQueryExecution"/> is true
    /// AND the caller has passed <see cref="ISqlGuardian"/> validation.
    /// Disabled by default.
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// Activates raw SQL execution path. Defaults to FALSE.
    /// Must be explicitly opted-in per call; never rely on caller default.
    /// </summary>
    public bool EnableDirectQueryExecution { get; init; } = false;

    // ── Parameters ────────────────────────────────────────────────────────────

    /// <summary>
    /// Named parameters bound safely via Dapper.
    /// Keys must match @param placeholders in the query.
    /// </summary>
    public JsonDocument? InputParameters { get; init; }

    // ── Pagination ────────────────────────────────────────────────────────────

    public int Count { get; init; } = 10;
    public int PageNumber { get; init; } = 1;

    // ── Server-side sorting ───────────────────────────────────────────────────

    public bool EnableServerSideSorting { get; init; } = false;

    /// <summary>
    /// Validated against INFORMATION_SCHEMA column whitelist before use.
    /// </summary>
    public string? SortField { get; init; }
    public SortDirection SortDirection { get; init; } = SortDirection.Asc;

    // ── Server-side filtering ─────────────────────────────────────────────────

    public bool EnableServerSideFiltering { get; init; } = false;

    /// <summary>
    /// Column names inside each condition are whitelist-validated before query build.
    /// </summary>
    public IReadOnlyList<FilterCondition> FilterConditions { get; init; } = [];

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>Global search — always bound as a parameter, never interpolated.</summary>
    public string? SearchText { get; init; }

    // ── Timezone ──────────────────────────────────────────────────────────────

    public string? FetchTimezone { get; init; }
}

public sealed record FilterCondition
{
    /// <summary>Column name — validated via schema whitelist before query build.</summary>
    public string Column { get; init; } = string.Empty;
    /// <summary>Column name — validated against INFORMATION_SCHEMA whitelist.</summary>
    public required string Field { get; init; }
    public string? Operator { get; init; }
    /// <summary>Value is always bound as a parameter — never interpolated.</summary>
    public object? Value { get; init; }
}
