namespace DataEngine.Core.Interfaces;

/// <summary>
/// Validates table/entity names against an allowlist to prevent injection.
/// </summary>
public interface ITableNameValidator
{
    Task EnsureAllowedAsync(string tableName, CancellationToken cancellationToken = default);
}