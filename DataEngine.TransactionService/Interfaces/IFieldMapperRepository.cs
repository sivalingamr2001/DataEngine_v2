using DataEngine.TransactionService.Domain;
using System.Data;

namespace DataEngine.TransactionService.Interfaces;

/// <summary>
/// Interacts with engine mapping tables to load dynamic validation properties.
/// </summary>
public interface IFieldMapperRepository
{
    /// <summary>
    /// Extracts a cached array slice listing active metadata constraints matching a table target.
    /// </summary>
    Task<List<FieldMapper>> GetFieldMappersAsync(string tableName, IDbConnection connection);
}
