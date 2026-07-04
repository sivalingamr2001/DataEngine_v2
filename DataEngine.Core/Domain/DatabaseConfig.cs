namespace DataEngine.ReaderService.Domain;

/// <summary>Database connection and behavior configuration.</summary>
public sealed record DatabaseConfig
{
    public required List<string> ConnectionString { get; init; }

    public DatabaseProvider Provider { get; init; } = DatabaseProvider.MySQL;

    public string? DefaultTimezone { get; init; } = "UTC";

    public int MaxPageSize { get; init; } = 1000;

    public bool EnableDirectQueryExecution { get; set; } = true;

    public int MaxDirectQueryLength { get; set; } = 10000;

    public int MaxRetryCount { get; init; } = 3;

    public int RetryDelayMs { get; init; } = 200;
}

public enum DatabaseProvider
{
    MySQL,
    Oracle
}
