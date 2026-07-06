namespace DataEngine.Core.Security;

public interface ITableNameValidator
{
    Task EnsureAllowedAsync(string tableName, CancellationToken ct = default);
}
