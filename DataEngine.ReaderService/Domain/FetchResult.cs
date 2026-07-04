namespace DataEngine.ReaderService.Domain;

/// <summary>Generic paginated result envelope returned by FetchQueryEngine.</summary>
public sealed record FetchResult<T>
{
    public IReadOnlyList<T> Data { get; init; } = [];
    public int TotalCount { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => PageNumber < TotalPages;
    public TimeSpan ExecutionTime { get; init; }
    public string? Message { get; init; }
    public bool Success { get; init; }
}
