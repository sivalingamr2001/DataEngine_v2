namespace DataEngine.Core.Configuration;

/// <summary>
/// Security-related configuration for authentication and table access control.
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// When true, all API endpoints require authentication. Defaults to false for backward compatibility.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>
    /// When true, only tables listed in <see cref="AllowedTables"/> may be accessed.
    /// When false, any syntactically valid identifier is permitted (legacy behavior).
    /// </summary>
    public bool EnforceTableAllowlist { get; set; }

    /// <summary>
    /// Explicit allowlist of table/entity names permitted for read/write operations.
    /// Comparison is case-insensitive.
    /// </summary>
    public List<string> AllowedTables { get; set; } = [];

    /// <summary>
    /// Optional shared API key. When set, requests must include header X-Api-Key with this value
    /// (unless JWT authentication is used).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// JWT bearer authentication settings.
    /// </summary>
    public JwtAuthOptions Jwt { get; set; } = new();
}

public sealed class JwtAuthOptions
{
    public bool Enabled { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>Symmetric signing key for development/simple deployments.</summary>
    public string? SigningKey { get; set; }
}
