using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using System.Text.RegularExpressions;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// Custom exception thrown when a SQL statement violates defined validation or security rules.
/// </summary>
public class SqlValidationException : Exception
{
    public SqlValidationException(string message) : base(message) { }
}

/// <summary>
/// Defensive SQL validation manager checking query text strings for safety, structural balance, and processing complexity.
/// </summary>
public class SqlGuardian : ISqlGuardian
{
    private static readonly string[] ProhibitedKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TRUNCATE",
        "MERGE", "REPLACE", "EXEC", "EXECUTE", "CALL", "GRANT", "REVOKE",
        "COMMIT", "ROLLBACK", "SAVEPOINT", "SET", "DECLARE", "BEGIN",
        "END", "IF", "WHILE", "FOR", "LOOP", "CURSOR", "PROCEDURE",
        "FUNCTION", "TRIGGER", "INDEX", "VIEW", "SCHEMA", "DATABASE",
        "TABLE", "COLUMN", "CONSTRAINT", "SEQUENCE", "COMMENT"
    ];

    private static readonly Regex JoinRegex = new(@"\bJOIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SelectRegex = new(@"\bSELECT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnionRegex = new(@"\bUNION\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SingleLineCommentRegex = new(@"--.*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MultiLineCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Validates read-only query structures against keyword constraints and malformed syntax.
    /// </summary>
    public void ValidateReadOnlyQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlValidationException("SQL query cannot be null or empty");
        }

        var cleanSql = RemoveComments(sql);
        cleanSql = NormalizeWhitespace(cleanSql);

        ValidateProhibitedKeywords(cleanSql);
        ValidateSelectOnly(cleanSql);
        ValidateSuspiciousPatterns(cleanSql);
        ValidateParenthesesBalance(cleanSql);
    }

    /// <summary>
    /// Validates dynamic query requests against runtime safety limits, input length rules, and injection hazards.
    /// </summary>
    public void ValidateDirectQuery(string sql, DatabaseConfig config)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SqlValidationException("Direct query cannot be null or empty");
        }

        if (config.MaxDirectQueryLength <= 0)
        {
            throw new SqlValidationException("Direct query execution is not enabled. Please contact your administrator.");
        }

        if (sql.Length > config.MaxDirectQueryLength)
        {
            throw new SqlValidationException($"Query exceeds maximum allowed length of {config.MaxDirectQueryLength} characters");
        }

        ValidateReadOnlyQuery(sql);
        ValidateDirectQueryComplexity(sql);
        ValidateDirectQuerySecurity(sql);
    }

    private void ValidateDirectQueryComplexity(string sql)
    {
        var cleanSql = RemoveComments(sql);

        var joinCount = JoinRegex.Matches(cleanSql).Count;
        if (joinCount > 10)
        {
            throw new SqlValidationException($"Query contains too many JOINs ({joinCount}). Maximum allowed is 10.");
        }

        var subqueryCount = SelectRegex.Matches(cleanSql).Count - 1;
        if (subqueryCount > 5)
        {
            throw new SqlValidationException($"Query contains too many subqueries ({subqueryCount}). Maximum allowed is 5.");
        }

        var unionCount = UnionRegex.Matches(cleanSql).Count;
        if (unionCount > 3)
        {
            throw new SqlValidationException($"Query contains too many UNION operations ({unionCount}). Maximum allowed is 3.");
        }
    }

    private void ValidateDirectQuerySecurity(string sql)
    {
        var cleanSql = RemoveComments(sql);

        var enhancedSuspiciousPatterns = new[]
        {
            @"\bxp_cmdshell\b", @"\bsp_executesql\b", @"\bEXEC\s*\(", @"\bEXECUTE\s*\(",
            @";\s*--", @"\bUNION\s+ALL\s+SELECT\b", @"\bINTO\s+OUTFILE\b", @"\bLOAD_FILE\b",
            @"\bINTO\s+DUMPFILE\b", @"\bSLEEP\s*\(", @"\bWAITFOR\s+DELAY\b", @"\bBENCHMARK\s*\(",
            @"\bPG_SLEEP\s*\(", @"@@\w+", @"\bINFORMATION_SCHEMA\b", @"\bSYS\.\w+",
            @"\bMASTER\.\w+", @"0x[0-9a-fA-F]+", @"\bCHAR\s*\(\s*\d+\s*\)", @"\bASCII\s*\(",
            @"\bCONCAT\s*\(", @"'\s*\+\s*'", @"\|\|", @"\bCAST\s*\(\s*0x", @"\bCONVERT\s*\(\s*\w+\s*,\s*0x"
        };

        foreach (var pattern in enhancedSuspiciousPatterns)
        {
            if (Regex.IsMatch(cleanSql, pattern, RegexOptions.IgnoreCase))
            {
                throw new SqlValidationException("Direct query contains potentially dangerous constructs and cannot be executed.");
            }
        }

        ValidateBlindInjectionPatterns(cleanSql);
        ValidateTimeBasedInjectionPatterns(cleanSql);
    }

    private void ValidateBlindInjectionPatterns(string sql)
    {
        var blindInjectionPatterns = new[]
        {
            @"1\s*=\s*1", @"1\s*=\s*2", @"'\s*=\s*'",
            @"'\s*OR\s*'1'\s*=\s*'1", @"'\s*AND\s*'1'\s*=\s*'1",
            @"\bOR\s+1\s*=\s*1\b", @"\bAND\s+1\s*=\s*1\b",
            @"'\s*OR\s*'.*?'\s*=\s*'", @"'\s*AND\s*'.*?'\s*=\s*'"
        };

        foreach (var pattern in blindInjectionPatterns)
        {
            if (Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase))
            {
                throw new SqlValidationException("Direct query contains patterns commonly used in SQL injection attacks.");
            }
        }
    }

    private void ValidateTimeBasedInjectionPatterns(string sql)
    {
        var timeBasedPatterns = new[]
        {
            @"\bIF\s*\(", @"\bCASE\s+WHEN\b", @"\bWAITFOR\b",
            @"\bSLEEP\b", @"\bPG_SLEEP\b", @"\bBENCHMARK\b", @"\bHEAVY_QUERY\b"
        };

        foreach (var pattern in timeBasedPatterns)
        {
            if (Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase))
            {
                throw new SqlValidationException("Direct query contains time-based patterns that could be used for injection attacks.");
            }
        }
    }

    private string RemoveComments(string sql)
    {
        sql = SingleLineCommentRegex.Replace(sql, "");
        return MultiLineCommentRegex.Replace(sql, "");
    }

    private string NormalizeWhitespace(string sql)
    {
        return WhitespaceRegex.Replace(sql.Trim(), " ");
    }

    private void ValidateProhibitedKeywords(string sql)
    {
        foreach (var keyword in ProhibitedKeywords)
        {
            var pattern = $@"\b{Regex.Escape(keyword)}\b";
            if (Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase))
            {
                throw new SqlValidationException($"Prohibited SQL keyword detected: {keyword}. Only SELECT queries are allowed.");
            }
        }
    }

    private void ValidateSelectOnly(string sql)
    {
        var trimmedSql = sql.TrimStart();
        if (!trimmedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlValidationException("Only SELECT statements are allowed. Query must start with SELECT.");
        }

        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (statements.Length > 1)
        {
            var nonEmptyStatements = statements.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (nonEmptyStatements.Length > 1)
            {
                throw new SqlValidationException("Multiple SQL statements are not allowed. Only single SELECT queries are permitted.");
            }
        }
    }

    private void ValidateSuspiciousPatterns(string sql)
    {
        var suspiciousPatterns = new[]
        {
            @"\bxp_cmdshell\b", @"\bsp_executesql\b", @"\bEXEC\s*\(", @"\bEXECUTE\s*\(",
            @";\s*--", @"\bUNION\s+ALL\s+SELECT\b", @"\bINTO\s+OUTFILE\b", @"\bLOAD_FILE\b", @"\bINTO\s+DUMPFILE\b"
        };

        foreach (var pattern in suspiciousPatterns)
        {
            if (Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase))
            {
                throw new SqlValidationException("Suspicious SQL pattern detected. This query contains potentially dangerous constructs.");
            }
        }
    }

    private void ValidateParenthesesBalance(string sql)
    {
        int openCount = 0;
        int closeCount = 0;

        foreach (char c in sql)
        {
            if (c == '(') openCount++;
            if (c == ')') closeCount++;
        }

        if (openCount != closeCount)
        {
            throw new SqlValidationException("Unbalanced parentheses detected in SQL query.");
        }
    }
}
