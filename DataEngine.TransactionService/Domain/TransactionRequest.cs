using System.Text.Json.Serialization;

namespace DataEngine.TransactionService.Domain;

/// <summary>
/// Request payload for <see cref="ITransactionEngine.ProcessTransactionAsync"/>.
/// Supports create, update, and delete operations on main and child tables.
/// </summary>
public sealed class TransactionRequest
{
    /// <summary>
    /// Main table row data for INSERT or UPDATE operations.
    /// Keyed by entity/table name. Presence of a non-empty primary key value triggers UPDATE; absence triggers INSERT.
    /// </summary>
    [JsonPropertyName("extendedProperties")]
    public Dictionary<string, object> ExtendedProperties { get; set; } = new();

    /// <summary>
    /// Child table rows to INSERT or UPDATE, keyed by entity/table name.
    /// Presence of a non-empty primary key value triggers UPDATE; absence triggers INSERT.
    /// </summary>
    [JsonPropertyName("renProps")]
    public Dictionary<string, List<Dictionary<string, object>>> RenProps { get; set; } = new();

    /// <summary>
    /// Rows to DELETE, keyed by entity/table name.
    /// Primary key must be present in every delete row.
    /// </summary>
    [JsonPropertyName("delProps")]
    public Dictionary<string, List<Dictionary<string, object>>> DelProps { get; set; } = new();

    /// <summary>
    /// The target database table name for this transaction.
    /// </summary>
    [JsonPropertyName("transactionEntityName")]
    public string TransactionEntityName { get; set; } = string.Empty;

    /// <summary>
    /// Caller-supplied correlation/idempotency ID. Written to all logs.
    /// </summary>
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>
    /// The user initiating the transaction. Written to audit logs.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;
}
