using DataEngine.Core.Configuration;

namespace DataEngine.Core.Interfaces;

/// <summary>
/// Security boundary validation engine inspecting SQL constructs and fields for injection vectors.
/// </summary>
public interface ISqlGuardian
{
    /// <summary>
    /// Validates basic Read-Only query constraints on database strings.
    /// </summary>
    void ValidateReadOnlyQuery(string sql);

    /// <summary>
    /// Validates direct queries, enforcing read-only constraint and complexity checks.
    /// </summary>
    void ValidateDirectQuery(string sql, DatabaseOptions options);

    /// <summary>
    /// Validates query complexity limits (joins, subqueries, unions, dangerous patterns).
    /// Applied to all executed SQL regardless of source.
    /// </summary>
    void ValidateQueryComplexity(string sql, DatabaseOptions options);

    /// <summary>
    /// Validates that a config-driven SQL identifier (table/column name) is safe.
    /// </summary>
    void ValidateConfigIdentifier(string? identifier, string context);

    /// <summary>
    /// Validates that a dynamic column/field identifier used in filtering matches strict structural patterns.
    /// </summary>
    void ValidateFieldName(string? fieldName);

    /// <summary>
    /// Validates that a dynamic column used for server-side sorting contains no injection payloads.
    /// </summary>
    void ValidateSortField(string? fieldName);
}
