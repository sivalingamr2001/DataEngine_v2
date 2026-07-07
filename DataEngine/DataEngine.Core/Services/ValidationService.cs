using DataEngine.Core.Configuration;
using DataEngine.Core.Domain;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataEngine.Core.Services;

/// <summary>
/// Validates transaction data against configured or database-stored entity rules.
/// </summary>
public sealed class ValidationService : IValidationService
{
    private readonly IOptions<DataEngineOptions> _options;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ValidationService> _logger;

    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromMinutes(10);

    public ValidationService(
        IOptions<DataEngineOptions> options,
        IDbConnectionFactory connectionFactory,
        IMemoryCache cache,
        ILogger<ValidationService> logger)
    {
        _options = options;
        _connectionFactory = connectionFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(
        string entityName,
        Dictionary<string, object> data,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var config = await GetValidationConfigAsync(entityName, transaction, cancellationToken);
        if (config is null || (config.Fields.Count == 0 && config.BusinessRules.Count == 0))
        {
            return new ValidationResult { IsValid = true };
        }

        var errors = new List<ValidationError>();

        foreach (var field in config.Fields)
        {
            data.TryGetValue(field.FieldName, out var rawValue);
            var value = UnwrapValue(rawValue);
            var display = field.DisplayName ?? field.FieldName;

            foreach (var rule in field.Validation)
            {
                if (!EvaluateWhen(rule.When, data))
                    continue;

                var error = EvaluateFieldRule(field.FieldName, display, value, rule);
                if (error is not null)
                    errors.Add(error);
            }
        }

        foreach (var rule in config.BusinessRules)
        {
            var error = EvaluateBusinessRule(rule, data);
            if (error is not null)
                errors.Add(error);
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Summary = errors.Count == 0 ? string.Empty : $"{errors.Count} validation error(s)."
        };
    }

    public async Task<ValidationConfiguration?> GetValidationConfigAsync(
        string entityName,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"validation::{entityName}";
        if (_cache.TryGetValue(cacheKey, out ValidationConfiguration? cached))
            return cached;

        if (_options.Value.Validation.Entities.TryGetValue(entityName, out var fromConfig))
        {
            _cache.Set(cacheKey, fromConfig, ConfigCacheTtl);
            return fromConfig;
        }

        var tableName = _options.Value.Validation.ValidationConfigTableName;
        if (string.IsNullOrWhiteSpace(tableName))
            return null;

        try
        {
            var connection = transaction?.Connection
                ?? await _connectionFactory.CreateConnectionAsync(cancellationToken);

            var shouldDispose = transaction?.Connection is null;
            try
            {
                var strategy = _connectionFactory.GetCurrentStrategy();
                var sql = $"""
                    SELECT validation_config_json
                    FROM {strategy.QuoteIdentifier(tableName)}
                    WHERE entity_name = @entityName AND is_active = 1
                    LIMIT 1
                    """;

                var json = await connection.QueryFirstOrDefaultAsync<string>(
                    new CommandDefinition(sql, new { entityName }, transaction, cancellationToken: cancellationToken));

                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var parsed = JsonSerializer.Deserialize<ValidationConfiguration>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed is not null)
                    _cache.Set(cacheKey, parsed, ConfigCacheTtl);

                return parsed;
            }
            finally
            {
                if (shouldDispose && connection is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (shouldDispose && connection is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load validation config for entity {Entity} from database.", entityName);
            return null;
        }
    }

    public async Task<bool> HasValidationConfigAsync(
        string entityName,
        CancellationToken cancellationToken = default)
    {
        var config = await GetValidationConfigAsync(entityName, null, cancellationToken);
        return config is not null && (config.Fields.Count > 0 || config.BusinessRules.Count > 0);
    }

    private static ValidationError? EvaluateFieldRule(string fieldName, string displayName, object? value, ValidationRule rule)
    {
        var ruleName = rule.Rule.ToUpperInvariant();
        var str = value?.ToString();

        return ruleName switch
        {
            "REQUIRED" when value is null || (str is not null && string.IsNullOrWhiteSpace(str))
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} is required.",
                    Rule = rule.Rule
                },

            "MINLENGTH" when str is not null && int.TryParse(rule.MinValue, out var minLen) && str.Length < minLen
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} must be at least {minLen} characters.",
                    Rule = rule.Rule
                },

            "MAXLENGTH" when str is not null && int.TryParse(rule.MaxValue, out var maxLen) && str.Length > maxLen
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} must be at most {maxLen} characters.",
                    Rule = rule.Rule
                },

            "PATTERN" when str is not null && !string.IsNullOrWhiteSpace(rule.Pattern)
                && !Regex.IsMatch(str, rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} format is invalid.",
                    Rule = rule.Rule
                },

            "MINVALUE" when value is not null && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var num)
                && decimal.TryParse(rule.MinValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var minVal)
                && num < minVal
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} must be at least {minVal}.",
                    Rule = rule.Rule
                },

            "MAXVALUE" when value is not null && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var num2)
                && decimal.TryParse(rule.MaxValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var maxVal)
                && num2 > maxVal
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} must be at most {maxVal}.",
                    Rule = rule.Rule
                },

            "ALLOWEDVALUES" when value is not null && rule.AllowedValues is { Count: > 0 }
                && !rule.AllowedValues.Any(v => v.Equals(str, StringComparison.OrdinalIgnoreCase))
                => new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = rule.ErrorMessage ?? $"{displayName} contains a disallowed value.",
                    Rule = rule.Rule
                },

            _ => null
        };
    }

    private static bool EvaluateWhen(string? whenExpression, Dictionary<string, object> data)
    {
        if (string.IsNullOrWhiteSpace(whenExpression))
            return true;

        // Simple "fieldName=expectedValue" conditional support
        var parts = whenExpression.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return true;

        if (!data.TryGetValue(parts[0], out var raw))
            return false;

        return string.Equals(UnwrapValue(raw)?.ToString(), parts[1], StringComparison.OrdinalIgnoreCase);
    }

    private static ValidationError? EvaluateBusinessRule(BusinessRule rule, Dictionary<string, object> data)
    {
        if (string.IsNullOrWhiteSpace(rule.Expression))
            return null;

        string[] operators = ["<=", ">=", "!=", "<", ">", "="];
        string? matchedOp = null;
        string[] parts = [];

        foreach (var op in operators)
        {
            if (rule.Expression.Contains(op))
            {
                matchedOp = op;
                parts = rule.Expression.Split(op, 2, StringSplitOptions.TrimEntries);
                break;
            }
        }

        if (matchedOp is null || parts.Length != 2)
            return null;

        var leftToken = parts[0];
        var rightToken = parts[1];

        var leftVal = ResolveTokenValue(leftToken, data);
        var rightVal = ResolveTokenValue(rightToken, data);

        bool isValid = CompareValues(leftVal, rightVal, matchedOp);
        if (!isValid)
        {
            return new ValidationError
            {
                FieldName = leftToken,
                DisplayName = leftToken,
                ErrorMessage = rule.ErrorMessage ?? $"Business rule validation failed: {rule.RuleName}.",
                Rule = rule.RuleName
            };
        }

        return null;
    }

    private static object? ResolveTokenValue(string token, Dictionary<string, object> data)
    {
        if (data.TryGetValue(token, out var val))
            return UnwrapValue(val);

        if (token.StartsWith('\'') && token.EndsWith('\''))
            return token[1..^1];
        if (token.StartsWith('"') && token.EndsWith('"'))
            return token[1..^1];

        if (decimal.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            return dec;

        if (bool.TryParse(token, out var b))
            return b;

        return token;
    }

    private static bool CompareValues(object? left, object? right, string op)
    {
        if (left is null || right is null)
        {
            return op switch
            {
                "=" => left == right,
                "!=" => left != right,
                _ => false
            };
        }

        if (TryGetDecimal(left, out var leftDec) && TryGetDecimal(right, out var rightDec))
        {
            return op switch
            {
                "=" => leftDec == rightDec,
                "!=" => leftDec != rightDec,
                "<" => leftDec < rightDec,
                ">" => leftDec > rightDec,
                "<=" => leftDec <= rightDec,
                ">=" => leftDec >= rightDec,
                _ => false
            };
        }

        if (TryGetDateTime(left, out var leftDt) && TryGetDateTime(right, out var rightDt))
        {
            return op switch
            {
                "=" => leftDt == rightDt,
                "!=" => leftDt != rightDt,
                "<" => leftDt < rightDt,
                ">" => leftDt > rightDt,
                "<=" => leftDt <= rightDt,
                ">=" => leftDt >= rightDt,
                _ => false
            };
        }

        var leftStr = left.ToString();
        var rightStr = right.ToString();

        return op switch
        {
            "=" => string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool TryGetDecimal(object value, out decimal result)
    {
        result = 0;
        if (value is decimal d) { result = d; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is double db) { result = (decimal)db; return true; }
        if (value is float f) { result = (decimal)f; return true; }

        var str = value.ToString();
        return decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryGetDateTime(object value, out DateTime result)
    {
        result = default;
        if (value is DateTime dt) { result = dt; return true; }
        var str = value.ToString();
        return DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static object? UnwrapValue(object? value)
    {
        if (value is JsonElement json)
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

        return value;
    }
}
