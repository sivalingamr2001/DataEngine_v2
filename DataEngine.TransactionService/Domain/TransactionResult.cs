namespace DataEngine.TransactionService.Domain;

/// <summary>Result of a transaction operation.</summary>
public sealed record TransactionResult
{
    public bool Success { get; init; }
    public string TransactionId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, object>? Data { get; init; }
    public IReadOnlyList<ValidationError>? ValidationErrors { get; init; }
}

public sealed record ValidationError
{
    public required string FieldName { get; init; }
    public required string ErrorMessage { get; init; }
    public string? Rule { get; init; }
}
