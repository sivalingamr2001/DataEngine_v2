using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DataEngine.Core.Security;

/// <summary>
/// Validates table/entity names against an optional allowlist to prevent unauthorized access.
/// </summary>
public sealed partial class TableNameValidator : ITableNameValidator
{
    private static readonly System.Text.RegularExpressions.Regex IdentifierShape =
        SafeIdentifierRegex();

    private readonly SecurityOptions _security;
    private readonly DataEngineOptions _engineOptions;

    public TableNameValidator(IOptions<DataEngineOptions> options)
    {
        _security = options.Value.Security;
        _engineOptions = options.Value;
    }

    public Task EnsureAllowedAsync(string tableName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !IdentifierShape.IsMatch(tableName))
        {
            throw new ArgumentException($"'{tableName}' is not a valid table identifier.", nameof(tableName));
        }

        // Prevent direct CRUD operations or direct access on system/metadata tables
        var systemTables = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "de_query_definitions",
            _engineOptions.Audit.ReadAuditTableName ?? "de_audit_read_log",
            _engineOptions.Audit.WriteAuditTableName ?? "de_audit_write_log"
        };

        foreach (var conn in _engineOptions.Connections)
        {
            if (!string.IsNullOrWhiteSpace(conn.FieldMappersTableName))
            {
                systemTables.Add(conn.FieldMappersTableName);
            }
        }

        if (systemTables.Contains(tableName))
        {
            throw new UnauthorizedAccessException($"Access to internal metadata table '{tableName}' is prohibited.");
        }

        if (_security.EnforceTableAllowlist)
        {
            if (_security.AllowedTables.Count == 0)
            {
                throw new InvalidOperationException(
                    "Table allowlist enforcement is enabled but AllowedTables is empty. " +
                    "Configure Security:AllowedTables or disable Security:EnforceTableAllowlist.");
            }

            var allowed = _security.AllowedTables.Any(t =>
                t.Equals(tableName, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
            {
                throw new UnauthorizedAccessException(
                    $"Table '{tableName}' is not in the configured allowlist.");
            }
        }

        return Task.CompletedTask;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex SafeIdentifierRegex();
}
