using DataEngine.Core.Domain;
using System.Data;

namespace DataEngine.Core.Interfaces;

/// <summary>Loads field mapping metadata for dynamic schema binding.</summary>
public interface IFieldMapperRepository
{
    Task<IReadOnlyList<FieldMapper>> GetFieldMappersAsync(
        string tableName,
        IDbConnection connection,
        CancellationToken cancellationToken = default);
}