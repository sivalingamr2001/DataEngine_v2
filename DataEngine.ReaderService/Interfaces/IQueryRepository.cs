using DataEngine.ReaderService.Domain;
using System.Data;

namespace DataEngine.ReaderService.Interfaces;

/// <summary>
/// Defines core data operations for interacting with query configuration definitions.
/// </summary>
public interface IQueryRepository
{
    /// <summary>
    /// Fetches an active metadata definition record matching an engine reference identifier or key string.
    /// </summary>
    Task<QueryDefinition?> GetQueryDefinitionAsync(int? id, string? definitionKey, IDbConnection connection);

    /// <summary>
    /// Extracts a comprehensive array list indexing all active engine configurations.
    /// </summary>
    Task<List<QueryDefinition>> GetAllQueryDefinitionsAsync(IDbConnection connection);
}
