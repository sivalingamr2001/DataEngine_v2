using DataEngine.Core.Domain;
using DataEngine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Data;
using System.Text.RegularExpressions;
using ValidationResult = DataEngine.Core.Domain.ValidationResult;

namespace DataEngine.Core.Services;

/// <summary>
/// Service for validating transaction data against validation rules stored in ValidationConfigs table
/// </summary>
public class ValidationService : IValidationService
{
    private readonly IDataProvider _dataProvider;
    private readonly ILogger<ValidationService> _logger;
    private readonly Dictionary<string, ValidationConfiguration?> _configCache = new();

    public ValidationService(IDataProvider dataProvider, ILogger<ValidationService> logger)
    {
        _dataProvider = dataProvider;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(string entityName, Dictionary<string, object> data, IDbTransaction? transaction = null)
    {
        var result = new ValidationResult { IsValid = true };

        try
        {
            // Get validation configuration for the entity
            var config = await GetValidationConfigAsync(entityName, transaction);

            if (config == null)
            {
                _logger.LogDebug("No validation configuration found for entity: {EntityName}. Skipping validation.", entityName);
                return result; // No validation rules = valid
            }

            // Validate field-level rules
            foreach (var fieldValidation in config.Fields)
            {
                var fieldValue = GetFieldValue(data, fieldValidation.FieldName);
                var fieldErrors = ValidateField(fieldValidation, fieldValue, data);

                if (fieldErrors.Any())
                {
                    result.IsValid = false;
                    result.Errors.AddRange(fieldErrors);
                }
            }

            // Validate business rules if all field validations pass
            if (result.IsValid && config.BusinessRules.Any())
            {
                var businessErrors = await ValidateBusinessRulesAsync(config.BusinessRules, data, entityName);
                if (businessErrors.Any())
                {
                    result.IsValid = false;
                    result.Errors.AddRange(businessErrors);
                }
            }

            result.Summary = result.IsValid
                ? "{MSG_ValidationPassed_}"
                : "{MSG_ValidationFailed_}";

            if (!result.IsValid)
            {
                _logger.LogWarning("Validation failed for entity {EntityName}: {Errors}",
                    entityName, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during validation for entity {EntityName}", entityName);
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                FieldName = "System",
                ErrorMessage = "{MSG_ValidationError_}",
                Rule = "System"
            });
            result.Summary = "{MSG_ValidationErrorOccurred_}";
            return result;
        }
    }

    public async Task<ValidationConfiguration?> GetValidationConfigAsync(string entityName, IDbTransaction? transaction = null)
    {
        // Check cache first
        if (_configCache.TryGetValue(entityName, out var cachedConfig))
        {
            return cachedConfig;
        }

        bool clearTransaction = false;

        try
        {
            if (transaction == null)
            {
                clearTransaction = true;
                var connection = await _dataProvider.GetConnectionAsync();
                transaction = await _dataProvider.BeginTransactionAsync(connection);
            }

            try
            {
                var query = @"SELECT ValidationConfig 
                                 FROM ValidationConfigs 
                                 WHERE EntityName = @entityName 
                                   AND IsActive = TRUE";

                var parameters = new Dictionary<string, object> { { "entityName", entityName } };

                var result = await _dataProvider.ExecuteScalarAsync(query, parameters, transaction);

                if (clearTransaction)
                    transaction.Commit();

                if (result == null || result == DBNull.Value)
                {
                    _configCache[entityName] = null;
                    return null;
                }

                var configJson = result.ToString() ?? "{}";
                var config = JsonConvert.DeserializeObject<ValidationConfiguration>(configJson);

                _configCache[entityName] = config;
                return config;
            }
            catch
            {
                if (clearTransaction)
                    transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving validation config for entity {EntityName}", entityName);
            return null;
        }
    }

    public async Task<bool> HasValidationConfigAsync(string entityName)
    {
        var config = await GetValidationConfigAsync(entityName);
        return config != null && (config.Fields.Any() || config.BusinessRules.Any());
    }

    private List<ValidationError> ValidateField(FieldValidation fieldValidation, object? fieldValue, Dictionary<string, object> allData)
    {
        var errors = new List<ValidationError>();

        foreach (var rule in fieldValidation.Validation)
        {
            var error = ValidateRule(rule, fieldValidation.FieldName, fieldValidation.DisplayName, fieldValue, allData);
            if (error != null)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    private ValidationError? ValidateRule(ValidationRule rule, string fieldName, string? displayName, object? fieldValue, Dictionary<string, object> allData)
    {
        try
        {
            // Check conditional validators first
            if (!string.IsNullOrEmpty(rule.When) && !EvaluateCondition(rule.When, allData))
                return null;

            if (!string.IsNullOrEmpty(rule.Unless) && EvaluateCondition(rule.Unless, allData))
                return null;

            switch (rule.Rule.ToLowerInvariant())
            {
                // String Validators
                case "mandatory":
                case "notempty":
                    return ValidateMandatory(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "minlength":
                    return ValidateMinLength(fieldName, displayName, fieldValue, rule.MinValue, rule.ErrorMessage);

                case "maxlength":
                    return ValidateMaxLength(fieldName, displayName, fieldValue, rule.MaxValue, rule.ErrorMessage);

                case "length":
                case "exactlength":
                    return ValidateExactLength(fieldName, displayName, fieldValue, rule.ExactLength, rule.ErrorMessage);

                case "pattern":
                case "matches":
                    return ValidatePattern(fieldName, displayName, fieldValue, rule.Pattern ?? rule.Matches, rule.ErrorMessage);

                case "regex":
                    return ValidatePattern(fieldName, displayName, fieldValue, rule.Regex, rule.ErrorMessage);

                case "email":
                case "emailaddress":
                    return ValidateEmail(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "url":
                    return ValidateUrl(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "creditcard":
                    return ValidateCreditCard(fieldName, displayName, fieldValue, rule.ErrorMessage);

                // Numeric Validators
                case "numeric":
                    return ValidateNumeric(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "numericrange":
                case "inclusivebetween":
                    return ValidateNumericRange(fieldName, displayName, fieldValue, rule.MinValue ?? rule.InclusiveBetween?.Split(',')[0],
                        rule.MaxValue ?? rule.InclusiveBetween?.Split(',').LastOrDefault(), rule.ErrorMessage);

                case "exclusivebetween":
                    return ValidateExclusiveBetween(fieldName, displayName, fieldValue, rule.ExclusiveBetween, rule.ErrorMessage);

                case "greaterthan":
                    return ValidateGreaterThan(fieldName, displayName, fieldValue, rule.GreaterThan, rule.ErrorMessage);

                case "greaterthanorequal":
                    return ValidateGreaterThanOrEqual(fieldName, displayName, fieldValue, rule.GreaterThanOrEqual, rule.ErrorMessage);

                case "lessthan":
                    return ValidateLessThan(fieldName, displayName, fieldValue, rule.LessThan, rule.ErrorMessage);

                case "lessthanorequal":
                    return ValidateLessThanOrEqual(fieldName, displayName, fieldValue, rule.LessThanOrEqual, rule.ErrorMessage);

                case "scaleprecision":
                    return ValidateScalePrecision(fieldName, displayName, fieldValue, rule.ScalePrecision, rule.ErrorMessage);

                // Date/Time Validators
                case "date":
                    return ValidateDate(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "daterange":
                    return ValidateDateRange(fieldName, displayName, fieldValue, rule.MinValue, rule.MaxValue, allData, rule.ErrorMessage);

                // Comparison Validators
                case "equal":
                    return ValidateEqual(fieldName, displayName, fieldValue, rule.Equal, rule.ErrorMessage);

                case "notequal":
                    return ValidateNotEqual(fieldName, displayName, fieldValue, rule.NotEqual, rule.ErrorMessage);

                case "null":
                    return ValidateNull(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "notnull":
                    return ValidateNotNull(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "empty":
                    return ValidateEmpty(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "guid":
                    return ValidateGuid(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "boolean":
                    return ValidateBoolean(fieldName, displayName, fieldValue, rule.ErrorMessage);

                case "enum":
                    return ValidateEnum(fieldName, displayName, fieldValue, rule.AllowedValues, rule.ErrorMessage);

                // Collection Validators
                case "musthaveatleastoneof":
                    return ValidateMustHaveAtLeastOneOf(fieldName, displayName, rule.MustHaveAtLeastOneOf, allData, rule.ErrorMessage);

                case "musthaveexactlyoneof":
                    return ValidateMustHaveExactlyOneOf(fieldName, displayName, rule.MustHaveExactlyOneOf, allData, rule.ErrorMessage);

                // Conditional Validators
                case "conditionalmandatory":
                    return ValidateConditionalMandatory(fieldName, displayName, fieldValue, rule.Condition, allData, rule.ErrorMessage);

                // Custom Validators
                case "custom":
                    return ValidateCustom(rule.RuleName, fieldName, displayName, fieldValue, rule, allData);

                default:
                    _logger.LogWarning("Unknown validation rule: {Rule} for field {FieldName}", rule.Rule, fieldName);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating rule {Rule} for field {FieldName}", rule.Rule, fieldName);
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = rule.ErrorMessage ?? GetDefaultMessageCode(rule.Rule),
                Rule = rule.Rule
            };
        }
    }

    private ValidationError? ValidateMandatory(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null ||
            (value is string str && string.IsNullOrWhiteSpace(str)) ||
            (value is DBNull))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldRequired_}",
                Rule = "Mandatory"
            };
        }
        return null;
    }

    private ValidationError? ValidateMinLength(string fieldName, string? displayName, object? value, string? minValue, string? errorMessage)
    {
        if (value == null) return null; // Mandatory check handles this

        if (int.TryParse(minValue, out var minLength))
        {
            var strValue = value.ToString() ?? string.Empty;
            if (strValue.Length < minLength)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = errorMessage ?? "{MSG_FieldMinLength_}",
                    Rule = "MinLength"
                };
            }
        }
        return null;
    }

    private ValidationError? ValidateMaxLength(string fieldName, string? displayName, object? value, string? maxValue, string? errorMessage)
    {
        if (value == null) return null;

        if (int.TryParse(maxValue, out var maxLength))
        {
            var strValue = value.ToString() ?? string.Empty;
            if (strValue.Length > maxLength)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = errorMessage ?? "{MSG_FieldMaxLength_}",
                    Rule = "MaxLength"
                };
            }
        }
        return null;
    }

    private ValidationError? ValidatePattern(string fieldName, string? displayName, object? value, string? pattern, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(pattern)) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!Regex.IsMatch(strValue, pattern))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldInvalidFormat_}",
                Rule = "Pattern"
            };
        }
        return null;
    }

    private ValidationError? ValidateEmail(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var emailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
        return ValidatePattern(fieldName, displayName, value, emailPattern,
            errorMessage ?? "{MSG_InvalidEmail_}");
    }

    private ValidationError? ValidatePhone(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var phonePattern = @"^[\d\s\-\+\(\)]+$";
        return ValidatePattern(fieldName, displayName, value, phonePattern,
            errorMessage ?? "{MSG_InvalidPhone_}");
    }

    private ValidationError? ValidateNumeric(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!decimal.TryParse(strValue, out _))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidNumber_}",
                Rule = "Numeric"
            };
        }
        return null;
    }

    private ValidationError? ValidateNumericRange(string fieldName, string? displayName, object? value, string? minValue, string? maxValue, string? errorMessage)
    {
        if (value == null) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue))
        {
            return ValidateNumeric(fieldName, displayName, value, null);
        }

        if (decimal.TryParse(minValue, out var min) && numericValue < min)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldMinValue_}",
                Rule = "NumericRange"
            };
        }

        if (decimal.TryParse(maxValue, out var max) && numericValue > max)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldMaxValue_}",
                Rule = "NumericRange"
            };
        }

        return null;
    }

    private ValidationError? ValidateDate(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        if (value is DateTime || value is DateOnly)
        {
            return null;
        }

        var strValue = value.ToString() ?? string.Empty;
        if (!DateTime.TryParse(strValue, out _) && !DateOnly.TryParse(strValue, out _))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidDate_}",
                Rule = "Date"
            };
        }
        return null;
    }

    private ValidationError? ValidateDateRange(string fieldName, string? displayName, object? value, string? minValue, string? maxValue, Dictionary<string, object> allData, string? errorMessage)
    {
        if (value == null) return null;

        DateTime? dateValue = null;
        if (value is DateTime dt) dateValue = dt;
        else if (value is DateOnly d) dateValue = d.ToDateTime(TimeOnly.MinValue);
        else if (DateTime.TryParse(value.ToString(), out var parsed)) dateValue = parsed;

        if (!dateValue.HasValue) return null;

        var date = dateValue.Value.Date;

        // Check minimum value
        if (!string.IsNullOrEmpty(minValue))
        {
            if (minValue.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                if (date < DateTime.Today)
                {
                    return new ValidationError
                    {
                        FieldName = fieldName,
                        DisplayName = displayName,
                        ErrorMessage = errorMessage ?? "{MSG_DateCannotBePast_}",
                        Rule = "DateRange"
                    };
                }
            }
            else if (allData.TryGetValue(minValue, out var minFieldValue))
            {
                DateTime? minDate = GetDateValue(minFieldValue);
                if (minDate.HasValue && date < minDate.Value.Date)
                {
                    return new ValidationError
                    {
                        FieldName = fieldName,
                        DisplayName = displayName,
                        ErrorMessage = errorMessage ?? "{MSG_DateMustBeAfter_}",
                        Rule = "DateRange"
                    };
                }
            }
        }

        // Check maximum value
        if (!string.IsNullOrEmpty(maxValue))
        {
            if (maxValue.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                if (date > DateTime.Today)
                {
                    return new ValidationError
                    {
                        FieldName = fieldName,
                        DisplayName = displayName,
                        ErrorMessage = errorMessage ?? "{MSG_DateCannotBeFuture_}",
                        Rule = "DateRange"
                    };
                }
            }
            else if (allData.TryGetValue(maxValue, out var maxFieldValue))
            {
                DateTime? maxDate = GetDateValue(maxFieldValue);
                if (maxDate.HasValue && date > maxDate.Value.Date)
                {
                    return new ValidationError
                    {
                        FieldName = fieldName,
                        DisplayName = displayName,
                        ErrorMessage = errorMessage ?? "{MSG_DateMustBeBefore_}",
                        Rule = "DateRange"
                    };
                }
            }
        }

        return null;
    }

    private DateTime? GetDateValue(object? value)
    {
        if (value == null) return null;
        if (value is DateTime dt) return dt;
        if (value is DateOnly d) return d.ToDateTime(TimeOnly.MinValue);
        if (DateTime.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    private ValidationError? ValidateGuid(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!Guid.TryParse(strValue, out _))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidGuid_}",
                Rule = "Guid"
            };
        }
        return null;
    }

    private ValidationError? ValidateBoolean(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!bool.TryParse(strValue, out _) &&
            !strValue.Equals("true", StringComparison.OrdinalIgnoreCase) &&
            !strValue.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            !strValue.Equals("1") && !strValue.Equals("0"))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidBoolean_}",
                Rule = "Boolean"
            };
        }
        return null;
    }

    private ValidationError? ValidateExactLength(string fieldName, string? displayName, object? value, string? exactLength, string? errorMessage)
    {
        if (value == null) return null;

        if (int.TryParse(exactLength, out var length))
        {
            var strValue = value.ToString() ?? string.Empty;
            if (strValue.Length != length)
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = errorMessage ?? "{MSG_FieldExactLength_}",
                    Rule = "ExactLength"
                };
            }
        }
        return null;
    }

    private ValidationError? ValidateUrl(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!Uri.TryCreate(strValue, UriKind.Absolute, out var uriResult) ||
            (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidUrl_}",
                Rule = "Url"
            };
        }
        return null;
    }

    private ValidationError? ValidateCreditCard(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null) return null;

        var strValue = value.ToString()?.Replace(" ", "").Replace("-", "") ?? string.Empty;

        if (!Regex.IsMatch(strValue, @"^\d{13,19}$"))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidCreditCard_}",
                Rule = "CreditCard"
            };
        }

        // Luhn algorithm validation
        if (!ValidateLuhnAlgorithm(strValue))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidCreditCard_}",
                Rule = "CreditCard"
            };
        }

        return null;
    }

    private bool ValidateLuhnAlgorithm(string cardNumber)
    {
        int sum = 0;
        bool isEvenPosition = false;

        for (int i = cardNumber.Length - 1; i >= 0; i--)
        {
            int digit = cardNumber[i] - '0';

            if (isEvenPosition)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            isEvenPosition = !isEvenPosition;
        }

        return sum % 10 == 0;
    }

    private ValidationError? ValidateExclusiveBetween(string fieldName, string? displayName, object? value, string? exclusiveBetween, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(exclusiveBetween)) return null;

        var parts = exclusiveBetween.Split(',');
        if (parts.Length != 2) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue) ||
            !decimal.TryParse(parts[0], out var min) ||
            !decimal.TryParse(parts[1], out var max))
        {
            return null;
        }

        if (numericValue <= min || numericValue >= max)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldExclusiveBetween_}",
                Rule = "ExclusiveBetween"
            };
        }

        return null;
    }

    private ValidationError? ValidateGreaterThan(string fieldName, string? displayName, object? value, string? greaterThan, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(greaterThan)) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue) ||
            !decimal.TryParse(greaterThan, out var compareValue))
        {
            return null;
        }

        if (numericValue <= compareValue)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldGreaterThan_}",
                Rule = "GreaterThan"
            };
        }

        return null;
    }

    private ValidationError? ValidateGreaterThanOrEqual(string fieldName, string? displayName, object? value, string? greaterThanOrEqual, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(greaterThanOrEqual)) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue) ||
            !decimal.TryParse(greaterThanOrEqual, out var compareValue))
        {
            return null;
        }

        if (numericValue < compareValue)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldGreaterThanOrEqual_}",
                Rule = "GreaterThanOrEqual"
            };
        }

        return null;
    }

    private ValidationError? ValidateLessThan(string fieldName, string? displayName, object? value, string? lessThan, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(lessThan)) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue) ||
            !decimal.TryParse(lessThan, out var compareValue))
        {
            return null;
        }

        if (numericValue >= compareValue)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldLessThan_}",
                Rule = "LessThan"
            };
        }

        return null;
    }

    private ValidationError? ValidateLessThanOrEqual(string fieldName, string? displayName, object? value, string? lessThanOrEqual, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(lessThanOrEqual)) return null;

        if (!decimal.TryParse(value.ToString(), out var numericValue) ||
            !decimal.TryParse(lessThanOrEqual, out var compareValue))
        {
            return null;
        }

        if (numericValue > compareValue)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldLessThanOrEqual_}",
                Rule = "LessThanOrEqual"
            };
        }

        return null;
    }

    private ValidationError? ValidateScalePrecision(string fieldName, string? displayName, object? value, string? scalePrecision, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(scalePrecision)) return null;

        var parts = scalePrecision.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var scale) || !int.TryParse(parts[1], out var precision))
            return null;

        if (!decimal.TryParse(value.ToString(), out var decimalValue))
            return null;

        var valueStr = decimalValue.ToString("F" + scale);
        var totalDigits = valueStr.Replace(".", "").Replace("-", "").Length;

        if (totalDigits > precision)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldScalePrecision_}",
                Rule = "ScalePrecision"
            };
        }

        return null;
    }

    private ValidationError? ValidateEqual(string fieldName, string? displayName, object? value, string? equal, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(equal)) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!strValue.Equals(equal, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldMustEqual_}",
                Rule = "Equal"
            };
        }

        return null;
    }

    private ValidationError? ValidateNotEqual(string fieldName, string? displayName, object? value, string? notEqual, string? errorMessage)
    {
        if (value == null || string.IsNullOrEmpty(notEqual)) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (strValue.Equals(notEqual, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldMustNotEqual_}",
                Rule = "NotEqual"
            };
        }

        return null;
    }

    private ValidationError? ValidateNull(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value != null && value != DBNull.Value)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldMustBeNull_}",
                Rule = "Null"
            };
        }

        return null;
    }

    private ValidationError? ValidateNotNull(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value == null || value == DBNull.Value)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_FieldCannotBeNull_}",
                Rule = "NotNull"
            };
        }

        return null;
    }

    private ValidationError? ValidateEmpty(string fieldName, string? displayName, object? value, string? errorMessage)
    {
        if (value != null && value != DBNull.Value)
        {
            var strValue = value.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(strValue))
            {
                return new ValidationError
                {
                    FieldName = fieldName,
                    DisplayName = displayName,
                    ErrorMessage = errorMessage ?? "{MSG_FieldMustBeEmpty_}",
                    Rule = "Empty"
                };
            }
        }

        return null;
    }

    private ValidationError? ValidateMustHaveAtLeastOneOf(string fieldName, string? displayName, List<string>? fieldNames, Dictionary<string, object> allData, string? errorMessage)
    {
        if (fieldNames == null || !fieldNames.Any()) return null;

        var hasValue = fieldNames.Any(fn =>
        {
            var value = GetFieldValue(allData, fn);
            return value != null && value != DBNull.Value && !string.IsNullOrWhiteSpace(value.ToString());
        });

        if (!hasValue)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_MustHaveAtLeastOneOf_}",
                Rule = "MustHaveAtLeastOneOf"
            };
        }

        return null;
    }

    private ValidationError? ValidateMustHaveExactlyOneOf(string fieldName, string? displayName, List<string>? fieldNames, Dictionary<string, object> allData, string? errorMessage)
    {
        if (fieldNames == null || !fieldNames.Any()) return null;

        var count = fieldNames.Count(fn =>
        {
            var value = GetFieldValue(allData, fn);
            return value != null && value != DBNull.Value && !string.IsNullOrWhiteSpace(value.ToString());
        });

        if (count != 1)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_MustHaveExactlyOneOf_}",
                Rule = "MustHaveExactlyOneOf"
            };
        }

        return null;
    }

    private ValidationError? ValidateEnum(string fieldName, string? displayName, object? value, List<string>? allowedValues, string? errorMessage)
    {
        if (value == null || allowedValues == null || !allowedValues.Any()) return null;

        var strValue = value.ToString() ?? string.Empty;
        if (!allowedValues.Any(v => v.Equals(strValue, StringComparison.OrdinalIgnoreCase)))
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = errorMessage ?? "{MSG_InvalidEnumValue_}",
                Rule = "Enum"
            };
        }
        return null;
    }

    private ValidationError? ValidateConditionalMandatory(string fieldName, string? displayName, object? value, string? condition, Dictionary<string, object> allData, string? errorMessage)
    {
        if (EvaluateCondition(condition, allData))
        {
            return ValidateMandatory(fieldName, displayName, value, errorMessage);
        }
        return null;
    }

    private bool EvaluateCondition(string? condition, Dictionary<string, object> allData)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;

        // Simple condition evaluation: "Status == 'Approved'"
        // Can be extended for more complex expressions
        if (condition.Contains("=="))
        {
            var parts = condition.Split(new[] { "==" }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var leftPart = parts[0].Trim();
                var rightPart = parts[1].Trim().Trim('\'', '"');

                if (allData.TryGetValue(leftPart, out var fieldValue))
                {
                    var fieldStr = fieldValue?.ToString() ?? string.Empty;
                    return fieldStr.Equals(rightPart, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return false;
    }

    private ValidationError? ValidateCustom(string? ruleName, string fieldName, string? displayName, object? value, ValidationRule rule, Dictionary<string, object> allData)
    {
        if (string.IsNullOrEmpty(ruleName)) return null;

        // Handle specific custom rules
        switch (ruleName)
        {
            case "MaxLeaveDays":
                return ValidateMaxLeaveDays(fieldName, displayName, value, rule, allData);
            default:
                _logger.LogWarning("Unknown custom rule: {RuleName}", ruleName);
                return null;
        }
    }

    private ValidationError? ValidateMaxLeaveDays(string fieldName, string? displayName, object? value, ValidationRule rule, Dictionary<string, object> allData)
    {
        // This validates that EndDate - StartDate <= maxValue days
        if (fieldName != "EndDate" || !allData.TryGetValue("StartDate", out var startDateObj))
            return null;

        var startDate = GetDateValue(startDateObj);
        var endDate = GetDateValue(value);

        if (!startDate.HasValue || !endDate.HasValue)
            return null;

        var days = (endDate.Value - startDate.Value).Days + 1;

        if (int.TryParse(rule.MaxValue, out var maxDays) && days > maxDays)
        {
            return new ValidationError
            {
                FieldName = fieldName,
                DisplayName = displayName,
                ErrorMessage = rule.ErrorMessage ?? "{MSG_LeaveExceedsMaxDays_}",
                Rule = "Custom:MaxLeaveDays"
            };
        }

        return null;
    }

    private Task<List<ValidationError>> ValidateBusinessRulesAsync(List<BusinessRule> businessRules, Dictionary<string, object> data, string entityName)
    {
        var errors = new List<ValidationError>();

        foreach (var rule in businessRules)
        {
            // Business rules typically require database queries or complex logic
            // For now, we'll log that they need implementation
            _logger.LogDebug("Business rule validation not fully implemented: {RuleName} for entity {EntityName}",
                rule.RuleName, entityName);

            // Placeholder for business rule validation
            // This would require additional context (database access, related records, etc.)
            // Example implementations could include:
            // - CheckLeaveBalance: Query leave balance table
            // - PreventOverlappingLeaves: Query existing leaves table
            // - MinimumNoticePeriod: Calculate days between dates
            // - MaxConsecutiveDays: Calculate days between StartDate and EndDate
        }

        return Task.FromResult(errors);
    }

    private object? GetFieldValue(Dictionary<string, object> data, string fieldName)
    {
        // Try exact match first
        if (data.TryGetValue(fieldName, out var value))
        {
            return value;
        }

        // Try case-insensitive match
        var key = data.Keys.FirstOrDefault(k => k.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (key != null)
        {
            return data[key];
        }

        return null;
    }

    /// <summary>
    /// Gets the default message code for a validation rule type
    /// </summary>
    private string GetDefaultMessageCode(string ruleType)
    {
        return ruleType.ToLowerInvariant() switch
        {
            // String Validators
            "mandatory" => "{MSG_FieldRequired_}",
            "notempty" => "{MSG_FieldRequired_}",
            "minlength" => "{MSG_FieldMinLength_}",
            "maxlength" => "{MSG_FieldMaxLength_}",
            "length" => "{MSG_FieldExactLength_}",
            "exactlength" => "{MSG_FieldExactLength_}",
            "pattern" => "{MSG_FieldInvalidFormat_}",
            "matches" => "{MSG_FieldInvalidFormat_}",
            "regex" => "{MSG_FieldInvalidFormat_}",
            "email" => "{MSG_InvalidEmail_}",
            "emailaddress" => "{MSG_InvalidEmail_}",
            "url" => "{MSG_InvalidUrl_}",
            "creditcard" => "{MSG_InvalidCreditCard_}",

            // Numeric Validators
            "numeric" => "{MSG_InvalidNumber_}",
            "numericrange" => "{MSG_FieldNumericRange_}",
            "inclusivebetween" => "{MSG_FieldNumericRange_}",
            "exclusivebetween" => "{MSG_FieldExclusiveBetween_}",
            "greaterthan" => "{MSG_FieldGreaterThan_}",
            "greaterthanorequal" => "{MSG_FieldGreaterThanOrEqual_}",
            "lessthan" => "{MSG_FieldLessThan_}",
            "lessthanorequal" => "{MSG_FieldLessThanOrEqual_}",
            "scaleprecision" => "{MSG_FieldScalePrecision_}",

            // Date/Time Validators
            "date" => "{MSG_InvalidDate_}",
            "daterange" => "{MSG_InvalidDateRange_}",

            // Comparison Validators
            "equal" => "{MSG_FieldMustEqual_}",
            "notequal" => "{MSG_FieldMustNotEqual_}",
            "null" => "{MSG_FieldMustBeNull_}",
            "notnull" => "{MSG_FieldCannotBeNull_}",
            "empty" => "{MSG_FieldMustBeEmpty_}",
            "guid" => "{MSG_InvalidGuid_}",
            "boolean" => "{MSG_InvalidBoolean_}",
            "enum" => "{MSG_InvalidEnumValue_}",

            // Collection Validators
            "musthaveatleastoneof" => "{MSG_MustHaveAtLeastOneOf_}",
            "musthaveexactlyoneof" => "{MSG_MustHaveExactlyOneOf_}",

            // Conditional Validators
            "conditionalmandatory" => "{MSG_FieldRequired_}",
            "when" => "{MSG_ConditionalValidationFailed_}",
            "unless" => "{MSG_ConditionalValidationFailed_}",

            // Custom Validators
            "custom" => "{MSG_ValidationError_}",
            "phone" => "{MSG_InvalidPhone_}",

            _ => "{MSG_ValidationError_}"
        };
    }
}