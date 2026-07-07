using DataEngine.Core.Configuration;
using DataEngine.Core.Exceptions;
using DataEngine.Core.Interfaces;
using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace DataEngine.Core.Services;

/// <summary>
/// Defensive SQL wall enforcing structure, limits, and dynamic field safety bounds.
/// </summary>
public sealed partial class SqlGuardian : ISqlGuardian
{
    [GeneratedRegex(@"^\s*SELECT\s", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex SelectStartRegex();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|TRUNCATE|MERGE|REPLACE|EXEC|EXECUTE|CALL|GRANT|REVOKE|COMMIT|ROLLBACK)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex DmlRegex();

    [GeneratedRegex(@"\b(JOIN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex JoinRegex();

    [GeneratedRegex(@"\b(SELECT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex SelectRegex();

    [GeneratedRegex(@"\b(UNION)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex UnionRegex();

    [GeneratedRegex(@"\b(WITH)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex WithRegex();

    [GeneratedRegex(@"--.*?$|/\*.*?\*/", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled, "en-US")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"'([^']|'')*'", RegexOptions.Compiled, "en-US")]
    private static partial Regex SingleQuoteStringRegex();

    [GeneratedRegex(@"""([^""]|"""")*""", RegexOptions.Compiled, "en-US")]
    private static partial Regex DoubleQuoteStringRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled, "en-US")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_\.]*$", RegexOptions.Compiled, "en-US")]
    private static partial Regex IdentifiersSanitizerRegex();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled, "en-US")]
    private static partial Regex ConfigIdentifierRegex();

    private static readonly FrozenSet<string> DangerousPatterns = new[]
    {
        @"\bxp_cmdshell\b", @"\bsp_executesql\b", @"\bINTO\s+OUTFILE\b",
        @"\bLOAD_FILE\b", @"\bINTO\s+DUMPFILE\b", @"\bSLEEP\s*\(",
        @"\bWAITFOR\b", @"\bBENCHMARK\s*\(", @"\bPG_SLEEP\s*\(",
        @"@@\w+", @"0x[0-9a-fA-F]+",
        @"\bCHAR\s*\(\s*\d+\s*\)"
    }.ToFrozenSet();

    public void ValidateReadOnlyQuery(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var clean = Normalize(sql);

        if (!SelectStartRegex().IsMatch(clean) && !WithRegex().IsMatch(clean))
            throw new SqlValidationException("Only SELECT statements are allowed. Query must start with SELECT or WITH.");

        if (DmlRegex().IsMatch(clean))
            throw new SqlValidationException("Prohibited SQL keyword detected. Only SELECT queries are allowed.");

        if (clean.Contains(';'))
        {
            var statements = clean.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (statements.Length > 1)
                throw new SqlValidationException("Multiple SQL statements are not allowed.");
        }

        var open = clean.Count(c => c == '(');
        var close = clean.Count(c => c == ')');
        if (open != close)
            throw new SqlValidationException("Unbalanced parentheses detected in SQL query.");
    }

    public void ValidateDirectQuery(string sql, DatabaseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (!options.EnableDirectQueryExecution)
            throw new SqlValidationException("Direct query execution is not enabled.");

        if (sql.Length > options.MaxDirectQueryLength)
            throw new SqlValidationException($"Query exceeds maximum length of {options.MaxDirectQueryLength} characters.");

        ValidateReadOnlyQuery(sql);
        ValidateQueryComplexity(sql, options);
    }

    public void ValidateQueryComplexity(string sql, DatabaseOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var clean = Normalize(sql);

        var joinCount = JoinRegex().Matches(clean).Count;
        if (joinCount > options.MaxJoinCount)
            throw new SqlValidationException($"Too many JOINs ({joinCount}). Max: {options.MaxJoinCount}.");

        var subqueryCount = Math.Max(0, SelectRegex().Matches(clean).Count - 1);
        if (WithRegex().IsMatch(clean))
            subqueryCount += 1;

        if (subqueryCount > options.MaxSubqueryCount)
            throw new SqlValidationException($"Too many subqueries ({subqueryCount}). Max: {options.MaxSubqueryCount}.");

        var unionCount = UnionRegex().Matches(clean).Count;
        if (unionCount > options.MaxUnionCount)
            throw new SqlValidationException($"Too many UNIONs ({unionCount}). Max: {options.MaxUnionCount}.");

        foreach (var pattern in DangerousPatterns)
        {
            if (Regex.IsMatch(clean, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                throw new SqlValidationException("Query contains potentially dangerous constructs.");
        }
    }

    public void ValidateFieldName(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new SqlValidationException("Filter condition target column identifier cannot be empty.");

        if (!IdentifiersSanitizerRegex().IsMatch(fieldName))
            throw new SqlValidationException($"Malicious filter identifier token context detected: '{fieldName}'");
    }

    public void ValidateSortField(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new SqlValidationException("Sort order sequence target column identifier cannot be empty.");

        if (!IdentifiersSanitizerRegex().IsMatch(fieldName))
            throw new SqlValidationException($"Malicious sorting structural identifier token context detected: '{fieldName}'");
    }

    public void ValidateConfigIdentifier(string? identifier, string context)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !ConfigIdentifierRegex().IsMatch(identifier))
            throw new ConfigurationException($"Invalid {context} identifier: '{identifier}'");
    }

    private static string Normalize(string sql)
    {
        var noStrings = SingleQuoteStringRegex().Replace(sql, "'STR'");
        noStrings = DoubleQuoteStringRegex().Replace(noStrings, "\"STR\"");

        var prev = noStrings;
        while (true)
        {
            var noComments = CommentRegex().Replace(prev, " ");
            if (noComments == prev)
                break;
            prev = noComments;
        }
        return WhitespaceRegex().Replace(prev, " ").Trim();
    }
}
