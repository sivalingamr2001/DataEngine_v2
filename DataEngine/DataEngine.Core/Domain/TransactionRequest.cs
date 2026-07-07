using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataEngine.Core.Domain;

/// <summary>
/// Request payload for transaction operations. Supports create, update, and delete.
/// </summary>
public sealed class TransactionRequest
{
    [JsonPropertyName("extendedProperties")]
    public Dictionary<string, object> ExtendedProperties { get; set; } = [];

    [JsonPropertyName("renProps")]
    public Dictionary<string, List<Dictionary<string, object>>> RenProps { get; set; } = [];

    [JsonPropertyName("delProps")]
    public Dictionary<string, List<Dictionary<string, object>>> DelProps { get; set; } = [];

    [JsonPropertyName("transactionEntityName")]
    public string TransactionEntityName { get; set; } = string.Empty;

    private object? _transactionIdValue = string.Empty;

    [JsonPropertyName("transactionId")]
    public object? TransactionIdValue
    {
        get => _transactionIdValue;
        set
        {
            _transactionIdValue = value;
            if (value == null)
            {
                TransactionId = Guid.Empty;
                ExternalTransactionId = null;
            }
            else if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var str = element.GetString();
                    _transactionIdValue = str;
                    ApplyTransactionIdString(str);
                }
            }
            else if (value is string str)
            {
                _transactionIdValue = str;
                ApplyTransactionIdString(str);
            }
            else if (value is Guid g)
            {
                _transactionIdValue = g;
                TransactionId = g;
                ExternalTransactionId = null;
            }
        }
    }

    private void ApplyTransactionIdString(string? str)
    {
        if (string.IsNullOrEmpty(str))
        {
            TransactionId = Guid.Empty;
            ExternalTransactionId = null;
        }
        else if (Guid.TryParse(str, out var g))
        {
            TransactionId = g;
            ExternalTransactionId = null;
        }
        else
        {
            ExternalTransactionId = str;
            TransactionId = Guid.NewGuid();
        }
    }

    [JsonIgnore]
    public string EffectiveTransactionId =>
        ExternalTransactionId ?? TransactionId.ToString();

    /// <summary>
    /// Preserves non-Guid transaction identifiers supplied by the client for correlation/audit.
    /// </summary>
    [JsonIgnore]
    public string? ExternalTransactionId { get; private set; }

    [JsonIgnore]
    public Guid TransactionId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("useModelBinding")]
    public bool UseModelBinding { get; set; }
}
