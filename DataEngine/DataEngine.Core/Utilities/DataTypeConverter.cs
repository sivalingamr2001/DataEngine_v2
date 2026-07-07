using System;
using System.Text.Json;

namespace DataEngine.Core.Utilities;

public static class DataTypeConverter
{
    public static object? CoerceValue(object? value, string? targetDataType)
    {
        if (value == null || value is DBNull) return null;
        if (string.IsNullOrWhiteSpace(targetDataType)) return value;

        if (value is JsonElement element)
        {
            value = UnwrapJsonElement(element);
            if (value == null) return null;
        }

        var typeUpper = targetDataType.ToUpperInvariant();

        try
        {
            switch (typeUpper)
            {
                case "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "NUMBER" when value is string strVal:
                    if (long.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLong)) 
                        return parsedLong;
                    break;

                case "DECIMAL" or "NUMERIC" or "FLOAT" or "DOUBLE" when value is string strVal:
                    if (decimal.TryParse(strVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedDecimal)) 
                        return parsedDecimal;
                    break;

                case "BOOL" or "BOOLEAN" when value is string strVal:
                    if (strVal == "1" || strVal.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (strVal == "0" || strVal.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    break;

                case "GUID" or "UUID" when value is string strVal:
                    if (Guid.TryParse(strVal, out var parsedGuid)) return parsedGuid;
                    break;

                case "DATETIME" or "TIMESTAMP" or "DATE" when value is string strVal:
                    if (DateTime.TryParse(strVal, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
                        return parsedDate;
                    break;
            }
        }
        catch
        {
            // Fallback to original value to prevent losing precision/format on parser glitches
        }

        return value;
    }

    private static object? UnwrapJsonElement(JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number => json.TryGetInt64(out var l) ? l : json.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => json.GetRawText()
        };
    }
}
