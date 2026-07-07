namespace DataEngine.Core.Domain;

/// <summary>Result of a transaction operation.</summary>
public sealed record TransactionResult
{
    public required bool Success { get; init; }

    public Guid TransactionId { get; init; } = Guid.NewGuid();

    public string Message { get; init; } = string.Empty;

    public Dictionary<string, object>? Data { get; init; }

    public IReadOnlyList<ValidationError> ValidationErrors { get; init; } = [];
}

public sealed record ValidationError
{
    public required string FieldName { get; init; }
    public string? DisplayName { get; init; }
    public required string ErrorMessage { get; init; }
    public string Rule { get; init; } = string.Empty;
}