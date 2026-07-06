using DataEngine.Core.Domain;
using System.Data;

namespace DataEngine.Core.Interfaces;

/// <summary>
/// Service interface for validating transaction data against validation rules
/// </summary>
public interface IValidationService
{
    /// <summary>
    /// Validates transaction request data against validation rules for the entity
    /// </summary>
    /// <param name="entityName">Name of the entity/table to validate</param>
    /// <param name="data">Data dictionary containing field values</param>
    /// <returns>Validation result with errors if any</returns>
    Task<DataEngine.Core.Domain.ValidationResult> ValidateAsync(string entityName, Dictionary<string, object> data, IDbTransaction? transaction = null);

    /// <summary>
    /// Gets validation configuration for an entity
    /// </summary>
    /// <param name="entityName">Name of the entity</param>
    /// <returns>Validation configuration or null if not found</returns>
    Task<ValidationConfiguration?> GetValidationConfigAsync(string entityName, IDbTransaction? transaction = null);

    /// <summary>
    /// Checks if validation configuration exists for an entity
    /// </summary>
    /// <param name="entityName">Name of the entity</param>
    /// <returns>True if validation config exists and is active</returns>
    Task<bool> HasValidationConfigAsync(string entityName);
}