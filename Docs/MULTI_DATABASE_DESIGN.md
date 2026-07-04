# MULTI_DATABASE_DESIGN.md — Provider-Based Architecture

## Goal

Replace the current hardcoded `switch (provider) { MySQL => ..., Oracle => ... }` scattered across `DatabaseConnectionFactory`, `DatabaseConnectionVerifier`, `MySqlGetDataService`/`OracleGetDataService`, and both `ServiceCollectionExtensions` classes with a single provider-strategy abstraction, so MySQL, SQL Server, PostgreSQL, Oracle, and SQLite are all first-class and adding a new one means implementing one interface, not editing five files.

## Core Abstractions

```csharp
namespace DataEngine.Core.Providers;

public enum DatabaseProvider { MySql, SqlServer, PostgreSql, Oracle, Sqlite }

/// <summary>Everything that differs between database engines lives behind this interface.</summary>
public interface IDbProviderStrategy
{
    DatabaseProvider Provider { get; }

    /// <summary>Creates a provider-specific, unopened connection for the given connection string.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>Quotes a validated identifier (table/column) using this provider's quoting rules.</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Builds a provider-correct paginated wrapper around a base SELECT (offset/fetch, LIMIT/OFFSET, ROWNUM, etc.).</summary>
    string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName);

    /// <summary>Builds the provider-correct "insert and return generated key" statement.</summary>
    string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn);

    /// <summary>Server-side current-timestamp expression (NOW(), SYSDATETIME(), CURRENT_TIMESTAMP, etc.).</summary>
    string CurrentTimestampExpression { get; }

    /// <summary>True if the underlying exception represents a transient/retryable condition (deadlock, connection reset, timeout).</summary>
    bool IsTransient(Exception ex);
}

public interface IDbProviderStrategyFactory
{
    IDbProviderStrategy Get(DatabaseProvider provider);
}
```

### Example implementations (abridged)

```csharp
namespace DataEngine.Core.Providers;

public sealed class MySqlProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.MySql;

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public string QuoteIdentifier(string identifier) => $"`{identifier}`";

    public string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName) =>
        $"{baseSelect} LIMIT {limitParamName} OFFSET {offsetParamName}";

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn) =>
        $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}); SELECT LAST_INSERT_ID();";

    public string CurrentTimestampExpression => "NOW()";

    public bool IsTransient(Exception ex) => ex is MySqlException { Number: 1213 or 1205 or 2013 or 2006 };
}

public sealed class SqlServerProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;
    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);
    public string QuoteIdentifier(string identifier) => $"[{identifier}]";

    public string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName) =>
        $"{baseSelect} ORDER BY (SELECT 1) OFFSET {offsetParamName} ROWS FETCH NEXT {limitParamName} ROWS ONLY";

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn) =>
        $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) OUTPUT INSERTED.{identityColumn} VALUES ({string.Join(", ", paramNames)});";

    public string CurrentTimestampExpression => "SYSUTCDATETIME()";

    public bool IsTransient(Exception ex) => ex is SqlException se && (se.Number is 1205 or -2 or 4060 or 40613 or 40197);
}

public sealed class OracleProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.Oracle;
    public DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString);
    public string QuoteIdentifier(string identifier) => $"\"{identifier.ToUpperInvariant()}\"";

    public string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName) =>
        $"SELECT * FROM ({baseSelect}) OFFSET {offsetParamName} ROWS FETCH NEXT {limitParamName} ROWS ONLY";

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn) =>
        $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}) RETURNING {identityColumn} INTO :generatedId";

    public string CurrentTimestampExpression => "SYSTIMESTAMP";

    public bool IsTransient(Exception ex) => ex is OracleException oe && (oe.Number is 12545 or 12541 or 3113 or 60);
}

public sealed class PostgreSqlProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.PostgreSql;
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);
    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName) =>
        $"{baseSelect} LIMIT {limitParamName} OFFSET {offsetParamName}";

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn) =>
        $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}) RETURNING {identityColumn};";

    public string CurrentTimestampExpression => "NOW()";

    public bool IsTransient(Exception ex) => ex is NpgsqlException { IsTransient: true };
}

public sealed class SqliteProviderStrategy : IDbProviderStrategy
{
    public DatabaseProvider Provider => DatabaseProvider.Sqlite;
    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);
    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string BuildPagedQuery(string baseSelect, string limitParamName, string offsetParamName) =>
        $"{baseSelect} LIMIT {limitParamName} OFFSET {offsetParamName}";

    public string BuildInsertReturningKey(string table, IReadOnlyList<string> columns, IReadOnlyList<string> paramNames, string? identityColumn) =>
        $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}); SELECT last_insert_rowid();";

    public string CurrentTimestampExpression => "CURRENT_TIMESTAMP";

    public bool IsTransient(Exception ex) => ex is SqliteException { SqliteErrorCode: 5 or 6 }; // SQLITE_BUSY / SQLITE_LOCKED
}
```

### Factory + DI registration

```csharp
namespace DataEngine.Core.Providers;

public sealed class DbProviderStrategyFactory(IEnumerable<IDbProviderStrategy> strategies) : IDbProviderStrategyFactory
{
    private readonly Dictionary<DatabaseProvider, IDbProviderStrategy> _map =
        strategies.ToDictionary(s => s.Provider);

    public IDbProviderStrategy Get(DatabaseProvider provider) =>
        _map.TryGetValue(provider, out var strategy)
            ? strategy
            : throw new NotSupportedException($"No provider strategy registered for '{provider}'.");
}

public static class ProviderServiceCollectionExtensions
{
    public static IServiceCollection AddDataEngineProviders(this IServiceCollection services)
    {
        services.AddSingleton<IDbProviderStrategy, MySqlProviderStrategy>();
        services.AddSingleton<IDbProviderStrategy, SqlServerProviderStrategy>();
        services.AddSingleton<IDbProviderStrategy, PostgreSqlProviderStrategy>();
        services.AddSingleton<IDbProviderStrategy, OracleProviderStrategy>();
        services.AddSingleton<IDbProviderStrategy, SqliteProviderStrategy>();
        services.AddSingleton<IDbProviderStrategyFactory, DbProviderStrategyFactory>();
        return services;
    }
}
```

### How this replaces existing switch statements

- `DatabaseConnectionFactory.CreateAndOpenConnectionAsync` becomes `_strategyFactory.Get(_config.Provider).CreateConnection(connectionString)` instead of its inline `switch`.
- `MySqlGetDataService`/`OracleGetDataService` collapse into a single `GenericGetDataService` that takes `IDbProviderStrategyFactory` and calls `strategy.BuildPagedQuery(...)`/`strategy.CurrentTimestampExpression` instead of hardcoding MySQL syntax — this is what actually fixes P1-1 (Oracle read gap) once an Oracle `SYS_REFCURSOR`-aware reader is added behind the same interface.
- `TransactionService.ProcessInsertAsync`/`ProcessUpdateAsync` use `strategy.QuoteIdentifier(...)` and `strategy.BuildInsertReturningKey(...)` instead of literal backticks and `NOW()`/`LAST_INSERT_ID()`.
- Resiliency (`RESILIENCY_DESIGN.md`) uses `strategy.IsTransient(ex)` as the Polly retry predicate instead of a MySQL-only assumption.

## Metadata Provider Pattern

```csharp
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> GetRegisteredTableNamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FieldMapper>> GetFieldMappersAsync(string tableName, CancellationToken ct = default);
}
```

`FieldMapperRepository`/`QueryRepository` become provider-agnostic implementations of `IMetadataProvider` since `de_field_mappers`/`de_query_definitions` are simple parameterized SELECTs that work unchanged across MySQL/PostgreSQL/SQL Server/SQLite; only Oracle's `RETURNING`/`SYS_REFCURSOR` conventions need a thin variant if stored procedures are introduced later.

## Query Builder Abstraction

```csharp
public interface IQueryBuilder
{
    (string Sql, Dictionary<string, object?> Parameters) BuildInsert(string table, IReadOnlyDictionary<string, object?> values, string? identityColumn);
    (string Sql, Dictionary<string, object?> Parameters) BuildUpdate(string table, string idColumn, object idValue, IReadOnlyDictionary<string, object?> values);
    (string Sql, Dictionary<string, object?> Parameters) BuildDelete(string table, string idColumn, object idValue);
    (string Sql, Dictionary<string, object?> Parameters) BuildPagedSelect(string baseSelect, IReadOnlyList<FilterCondition> filters, string? sortColumn, SortDirection sortDirection, int limit, int offset);
}
```

A single `DefaultQueryBuilder` implementation, parameterized by `IDbProviderStrategy`, replaces the ad hoc string building currently duplicated in `TransactionService` and `MySqlGetDataService`, and is the natural place to implement P1-2 (actually applying `FilterConditions`/`SortField`) with the same identifier-validation guarantee as P0-1 (every `Column`/`Field` passed through `ITableNameValidator`-style allow-list checks before being embedded).

## Migration Notes

- `DatabaseConfig.Provider` and `Enums.DatabaseProvider` should be consolidated into the single `DataEngine.Core.Providers.DatabaseProvider` enum above (resolves P2-4).
- Existing MySQL behavior (backtick quoting, `LAST_INSERT_ID()`, `SQL_CALC_FOUND_ROWS`) is preserved exactly inside `MySqlProviderStrategy`, so this is a refactor, not a behavior change, for the currently-working MySQL path.
