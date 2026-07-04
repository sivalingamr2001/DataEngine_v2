namespace DataEngine.ReaderService.Domain;

public sealed record QueryDefinition
{
    public int Id { get; init; }
    public required string DefinitionKey { get; init; }
    public required string TableName { get; init; }
    public string? Description { get; init; }
    public required string QueryText { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string CreatedBy { get; init; } = "System";
    public string? UpdatedBy { get; init; }
}
