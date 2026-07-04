using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DataEngine.Core.Services;

public class DataTypeConverter
{
    private readonly ILogger<DataTypeConverter> _logger;

    public DataTypeConverter(ILogger<DataTypeConverter> logger)
    {
        _logger = logger;
    }

    public object? ConvertValue(object? value, string dataType, string? defaultValue = null, string? fieldName = null, string? sequenceName = null, string? properties = null)
    {
        // Handle sequence-based generation for fields with "|Sequence|" in properties
        if (!string.IsNullOrEmpty(properties) && properties.Contains("|Sequence|") && !string.IsNullOrEmpty(sequenceName))
        {
            return GenerateSequenceValue(dataType, sequenceName);
        }

        // Handle auto-generation for fields with "Auto" in the name
        if (!string.IsNullOrEmpty(fieldName) && fieldName.ToLower().Contains("auto"))
        {
            return GenerateAutoValue(dataType);
        }

        // Handle null values with defaults
        if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
        {
            //return null;
            return GetDefaultValue(dataType, defaultValue);
        }

        // If value is provided, use the original conversion logic
        return ConvertValueInternal(value, dataType, defaultValue);
    }



    private object? ConvertValueInternal(object? value, string dataType, string? defaultValue = null)
    {
        // Handle null values with defaults
        if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
        {
            return GetDefaultValue(dataType, defaultValue);
        }

        try
        {
            var stringValue = value.ToString() ?? "";

            return dataType.ToLower() switch
            {
                // String types
                "string" or "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" =>
                    ConvertToString(value),

                // Integer types
                "int" or "integer" or "int32" => ConvertToInt32(stringValue, defaultValue),
                "bigint" or "long" or "int64" => ConvertToInt64(stringValue, defaultValue),
                "smallint" or "short" or "int16" => ConvertToInt16(stringValue, defaultValue),
                "tinyint" or "byte" => ConvertToByte(stringValue, defaultValue),

                // Decimal types
                "decimal" or "numeric" => ConvertToDecimal(stringValue, defaultValue),
                "float" or "real" or "single" => ConvertToFloat(stringValue, defaultValue),
                "double" => ConvertToDouble(stringValue, defaultValue),
                "money" or "smallmoney" => ConvertToDecimal(stringValue, defaultValue),

                // Boolean types
                "boolean" or "bool" or "bit" => ConvertToBoolean(stringValue, defaultValue),

                // Date/Time types
                "datetime" or "datetime2" or "timestamp" => ConvertToDateTime(stringValue, defaultValue),
                "date" => ConvertToDate(stringValue, defaultValue),
                "time" => ConvertToTime(stringValue, defaultValue),
                "datetimeoffset" => ConvertToDateTimeOffset(stringValue, defaultValue),

                // GUID types
                "guid" or "uuid" or "uniqueidentifier" => ConvertToGuid(stringValue, defaultValue),

                // Binary types (as byte arrays)
                "binary" or "varbinary" or "image" or "bytea" => ConvertToBinary(stringValue, defaultValue),

                // Enum (as string)
                "enum" => ConvertToString(value),

                // XML (as string)
                "xml" => ConvertToString(value),

                "json" or "jsonb" => ConvertToJson(value),

                // Default: return as-is
                _ => value
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert value '{Value}' to type '{DataType}', using default", value, dataType);
            return GetDefaultValue(dataType, defaultValue);
        }
    }

    private object? GetDefaultValue(string dataType, string? defaultValue)
    {
        // If explicit default provided, try to convert it
        if (!string.IsNullOrEmpty(defaultValue))
        {
            try
            {
                return ConvertValueInternal(defaultValue, dataType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert default value '{DefaultValue}' for type '{DataType}'", defaultValue, dataType);
            }
        }

        // Return type-appropriate defaults
        return null;
    }
    private string ConvertToJson(object? value)
    {
        if (value == null) return "{}";

        try
        {
            // If it's already a JSON string, return as-is
            if (value is string str)
            {
                str = str.Trim();
                if ((str.StartsWith("{") && str.EndsWith("}")) ||
                    (str.StartsWith("[") && str.EndsWith("]")))
                {
                    return str; // valid JSON
                }
            }

            // Otherwise serialize the object to JSON
            return SerializeObject(value);
        }
        catch
        {
            return "{}"; // fallback
        }
    }

    private string ConvertToString(object? value)
    {
        return value?.ToString() ?? string.Empty;
    }

    private int ConvertToInt32(string value, string? defaultValue)
    {
        if (int.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && int.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return 0;
    }

    private long ConvertToInt64(string value, string? defaultValue)
    {
        if (long.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && long.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return 0L;
    }

    private short ConvertToInt16(string value, string? defaultValue)
    {
        if (short.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && short.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return 0;
    }

    private byte ConvertToByte(string value, string? defaultValue)
    {
        if (byte.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && byte.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return 0;
    }

    private decimal ConvertToDecimal(string value, string? defaultValue)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && decimal.TryParse(defaultValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var defaultResult)) return defaultResult;
        return 0m;
    }

    private float ConvertToFloat(string value, string? defaultValue)
    {
        if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && float.TryParse(defaultValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var defaultResult)) return defaultResult;
        return 0f;
    }

    private double ConvertToDouble(string value, string? defaultValue)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && double.TryParse(defaultValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var defaultResult)) return defaultResult;
        return 0d;
    }

    private bool ConvertToBoolean(string value, string? defaultValue)
    {
        // Handle various boolean representations
        var lowerValue = value.ToLower();
        if (lowerValue is "true" or "1" or "yes" or "y" or "on") return true;
        if (lowerValue is "false" or "0" or "no" or "n" or "off") return false;

        if (bool.TryParse(value, out var result)) return result;

        // Try default value
        if (!string.IsNullOrEmpty(defaultValue))
        {
            var lowerDefault = defaultValue.ToLower();
            if (lowerDefault is "true" or "1" or "yes" or "y" or "on") return true;
            if (lowerDefault is "false" or "0" or "no" or "n" or "off") return false;
        }

        return false;
    }

    private DateTime ConvertToDateTime(string value, string? defaultValue)
    {
        if (DateTime.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && DateTime.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return DateTime.Now;
    }

    private DateTime ConvertToDate(string value, string? defaultValue)
    {
        if (DateTime.TryParse(value, out var result)) return result.Date;
        if (!string.IsNullOrEmpty(defaultValue) && DateTime.TryParse(defaultValue, out var defaultResult)) return defaultResult.Date;
        return DateTime.Today;
    }

    private TimeOnly ConvertToTime(string value, string? defaultValue)
    {
        if (TimeOnly.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && TimeOnly.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return TimeOnly.FromDateTime(DateTime.Now);
    }

    private DateTimeOffset ConvertToDateTimeOffset(string value, string? defaultValue)
    {
        if (DateTimeOffset.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && DateTimeOffset.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return DateTimeOffset.Now;
    }

    private Guid ConvertToGuid(string value, string? defaultValue)
    {
        if (Guid.TryParse(value, out var result)) return result;
        if (!string.IsNullOrEmpty(defaultValue) && Guid.TryParse(defaultValue, out var defaultResult)) return defaultResult;
        return Guid.NewGuid();
    }

    private byte[] ConvertToBinary(string value, string? defaultValue)
    {
        try
        {
            // Try base64 decode
            return Convert.FromBase64String(value);
        }
        catch
        {
            try
            {
                // Try hex decode
                return Convert.FromHexString(value);
            }
            catch
            {
                // Try default value
                if (!string.IsNullOrEmpty(defaultValue))
                {
                    try
                    {
                        return Convert.FromBase64String(defaultValue);
                    }
                    catch
                    {
                        try
                        {
                            return Convert.FromHexString(defaultValue);
                        }
                        catch
                        {
                            // Fall through to empty array
                        }
                    }
                }
                return Array.Empty<byte>();
            }
        }
    }

    private object GenerateSequenceValue(string dataType, string sequenceName)
    {
        // For now, return a placeholder that indicates sequence should be used
        // The actual sequence value will be handled by the database provider
        return dataType.ToLower() switch
        {
            "int" or "integer" or "int32" => $"NEXTVAL('{sequenceName}')",
            "bigint" or "long" or "int64" => $"NEXTVAL('{sequenceName}')",
            "string" or "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" =>
                $"NEXTVAL('{sequenceName}')",
            _ => $"NEXTVAL('{sequenceName}')"
        };
    }

    private object GenerateAutoValue(string dataType)
    {
        var random = new Random();

        return dataType.ToLower() switch
        {
            // GUID types - generate new GUID
            "guid" or "uuid" or "uniqueidentifier" => Guid.NewGuid(),

            // Integer types - generate random numbers
            "int" or "integer" or "int32" => random.Next(1000, 999999),
            "bigint" or "long" or "int64" => (long)random.Next(1000, 999999),
            "smallint" or "short" or "int16" => (short)random.Next(1000, 32767),
            "tinyint" or "byte" => (byte)random.Next(1, 255),

            // Decimal types - generate random decimals
            "decimal" or "numeric" => (decimal)(random.NextDouble() * 1000),
            "float" or "real" or "single" => (float)(random.NextDouble() * 1000),
            "double" => random.NextDouble() * 1000,
            "money" or "smallmoney" => (decimal)(random.NextDouble() * 1000),

            // String types - generate auto string
            "string" or "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" =>
                $"AUTO_{Guid.NewGuid().ToString("N")[..8].ToUpper()}",

            // Boolean - random true/false
            "boolean" or "bool" or "bit" => random.Next(0, 2) == 1,

            // Date/Time types - current time with random offset
            "datetime" or "datetime2" or "timestamp" => DateTime.Now.AddMinutes(random.Next(-1440, 1440)),
            "date" => DateTime.Today.AddDays(random.Next(-30, 30)),
            "time" => TimeOnly.FromDateTime(DateTime.Now.AddMinutes(random.Next(-720, 720))),
            "datetimeoffset" => DateTimeOffset.Now.AddMinutes(random.Next(-1440, 1440)),

            // Binary types - random bytes
            "binary" or "varbinary" or "image" or "bytea" => GenerateRandomBytes(16),

            // Default - return auto string
            _ => $"AUTO_{Guid.NewGuid().ToString("N")[..8].ToUpper()}"
        };
    }

    private byte[] GenerateRandomBytes(int length)
    {
        var random = new Random();
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    public bool IsValidDataType(string dataType)
    {
        var supportedTypes = new[]
        {
            "string", "varchar", "nvarchar", "char", "nchar", "text", "ntext",
            "int", "integer", "int32", "bigint", "long", "int64", "smallint", "short", "int16", "tinyint", "byte",
            "decimal", "numeric", "float", "real", "single", "double", "money", "smallmoney",
            "boolean", "bool", "bit",
            "datetime", "datetime2", "timestamp", "date", "time", "datetimeoffset",
            "guid", "uuid", "uniqueidentifier",
            "binary", "varbinary", "image", "bytea",
            "enum", "xml"
        };

        return supportedTypes.Contains(dataType.ToLower());
    }
}
