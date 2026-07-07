using DataEngine.Core.Domain;
using System.Data;

namespace DataEngine.Core.Interfaces;

/// <summary>Validates transaction data against entity rules.</summary>
public interface IValidationService
{
    Task<ValidationResult> ValidateAsync(
        string entityName,
        Dictionary<string, object> data,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<ValidationConfiguration?> GetValidationConfigAsync(
        string entityName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasValidationConfigAsync(
        string entityName,
        CancellationToken cancellationToken = default);
}