using DataEngine.Core.Domain;

namespace DataEngine.Core.Interfaces;

/// <summary>
/// Single entry point for every read/SELECT operation in the engine.
/// </summary>
public interface IReaderService
{
    Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct);
}
