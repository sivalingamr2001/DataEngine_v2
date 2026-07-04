using DataEngine.ReaderService.Domain;

namespace DataEngine.ReaderService.Interfaces;

public interface ISqlGuardian
{
    /// <summary>
    /// Validates standard read-only queries. Throws exception if validation fails.
    /// </summary>
    void ValidateReadOnlyQuery(string sql);

    /// <summary>
    /// Validates direct queries against strict complexity and security rules. Throws exception if validation fails.
    /// </summary>
    void ValidateDirectQuery(string sql, DatabaseConfig config);
}
