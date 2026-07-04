using DataEngine.ReaderService.Domain;

namespace DataEngine.ReaderService.Interfaces;

/// <summary>
/// Single entry point for every read/SELECT operation in the engine —
/// stored query, direct query, filtered, paginated, or searched.
/// Optimized for minimum allocation and maximum throughput.
/// </summary>
public interface IGetDataService
{
    Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct);
}
