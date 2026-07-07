using DataEngine.Core.Domain;
using System.Data;

namespace DataEngine.Core.Interfaces;

/// <summary>Repository for query definition metadata.</summary>
public interface IQueryRepository
{
    Task<QueryDefinition?> GetQueryDefinitionAsync(
        int? id,
        string? definitionKey,
        IDbConnection connection,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueryDefinition>> GetAllQueryDefinitionsAsync(
        IDbConnection connection,
        CancellationToken cancellationToken = default);
}