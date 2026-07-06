using System.Text.RegularExpressions;

namespace DataEngine.Core.Security;

public sealed partial class TableNameValidator : ITableNameValidator
{
    private static readonly Regex IdentifierShape = SafeIdentifierRegex();

    public Task EnsureAllowedAsync(string tableName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !IdentifierShape.IsMatch(tableName))
        {
            throw new ArgumentException($"'{tableName}' is not a valid table identifier.", nameof(tableName));
        }

        return Task.CompletedTask;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SafeIdentifierRegex();
}
