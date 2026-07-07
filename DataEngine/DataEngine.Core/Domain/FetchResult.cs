namespace DataEngine.Core.Domain;

/// <summary>Generic paginated result envelope.</summary>
public sealed record FetchResult<T>
{
    public required IReadOnlyList<T> Data { get; init; }
    public required int TotalCount { get; init; }
    public required int PageNumber { get; init; }
    public required int PageSize { get; init; }

    // CHANGED: Integer math, no floating point
    public int TotalPages => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 0;

    public bool HasNextPage => PageNumber < TotalPages;

    public TimeSpan ExecutionTime { get; init; }

    public string? Message { get; init; }

    public bool Success { get; init; }
}