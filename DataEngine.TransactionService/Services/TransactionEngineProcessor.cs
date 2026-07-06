using DataEngine.TransactionService.Domain;
using System.Text.Json;

namespace DataEngine.TransactionService.Services;

public interface ITransactionEngineProcessor
{
    void Prepare(TransactionRequest request, string? clientIp, string? correlationId);
}

public sealed class TransactionEngineProcessor : ITransactionEngineProcessor
{
    public void Prepare(TransactionRequest request, string? clientIp, string? correlationId)
    {
        if (request == null) return;

        // 1. Assign network context metrics passed from the API layer
        request.IpAddress ??= clientIp;
        request.CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? correlationId : request.CorrelationId;

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            request.CorrelationId = Guid.NewGuid().ToString();
        }

        // 2. Normalize and unwrap payload properties to prevent MySQL parameter crashes
        if (request.ExtendedProperties != null && request.ExtendedProperties.Count > 0)
        {
            var cleanProperties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kvp in request.ExtendedProperties)
            {
                object value = kvp.Value;
                if (kvp.Value is JsonElement jsonElement)
                {
                    value = jsonElement.ValueKind switch
                    {
                        JsonValueKind.String => jsonElement.GetString()!,
                        JsonValueKind.MarshalByRefObject => jsonElement.GetString()!,
                        JsonValueKind.Number => jsonElement.TryGetInt64(out long l) ? l : jsonElement.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null!,
                        _ => jsonElement.GetRawText()
                    };
                }

                if (value is string strVal && strVal.Contains('T') && (strVal.EndsWith('Z') || strVal.Contains('+')))
                {
                    if (DateTime.TryParse(strVal, out DateTime parsedDate))
                    {
                        value = parsedDate;
                    }
                }
                cleanProperties[kvp.Key] = value;
            }
            request.ExtendedProperties = cleanProperties;
        }
    }
}
