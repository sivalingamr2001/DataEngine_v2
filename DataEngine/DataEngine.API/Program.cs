using DataEngine.Core;
using DataEngine.Core.Configuration;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;
using DataEngine.API.Security;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting up DataEngine API Host...");

    var builder = WebApplication.CreateBuilder(args);

    Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

    builder.Logging.ClearProviders();
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddFixedWindowLimiter("api", opt =>
        {
            opt.PermitLimit = 100;
            opt.Window = TimeSpan.FromSeconds(10);
            opt.QueueLimit = 10;
            opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        });
    });

    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks();

    // ═══════════════════════════════════════════════════════════════════
    // NEW: Multi-provider DataEngine.Core registration
    // ═══════════════════════════════════════════════════════════════════

    // Parse all configured connections from appsettings.json
    var connections = ParseConnectionsFromConfiguration(builder.Configuration);

    if (connections.Count == 0)
    {
        Log.Fatal("No database connections configured. Check ConnectionStrings section in appsettings.json.");
        throw new InvalidOperationException("At least one database connection must be configured.");
    }

    // Ensure exactly one default connection
    if (connections.Count(c => c.IsDefault) != 1)
    {
        connections[0] = connections[0] with { IsDefault = true };
        Log.Warning("No explicit default connection set. Using '{Name}' as default.", connections[0].Name);
    }

    var securityOptions = new SecurityOptions();
    builder.Configuration.GetSection("Security").Bind(securityOptions);

    // Register DataEngine.Core with all providers
    builder.Services.AddDataEngine(options =>
    {
        options.Connections = connections;
        builder.Configuration.GetSection("Security").Bind(options.Security);
        builder.Configuration.GetSection("Audit").Bind(options.Audit);
        builder.Configuration.GetSection("Validation").Bind(options.Validation);
    });

    builder.Services.AddDataEngineAuthentication(securityOptions);

    Log.Information("Registered {Count} database provider(s): {Providers}",
        connections.Count,
        string.Join(", ", connections.Select(c => $"{c.Name} ({c.Provider})")));

    WebApplication app;
    try
    {
        // This is line 63 where your application is crashing!
        app = builder.Build();
    }
    catch (Exception ex)
    {
        // This breaks the application and prints out the exact missing interface name
        Log.Fatal(ex, "CRITICAL DI CONTAINER BREAKDOWN: {Message}", ex.Message);
        if (ex.InnerException != null)
        {
            Log.Fatal(ex.InnerException, "INNER CULPRIT DETAILS: {Message}", ex.InnerException.Message);
        }
        throw;
    }

    app.UseSerilogRequestLogging();

    // ═══════════════════════════════════════════════════════════════════
    // NEW: Connection health verification using IDbConnectionFactory
    // ═══════════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════════
    // FIXED: Asynchronous Scope initialization prevents IAsyncDisposable crashes
    // ═══════════════════════════════════════════════════════════════════
    try
    {
        // FIX: Changed 'using var' to 'await using var' so .NET runs DisposeAsync() on DbConnectionFactory
        await using var scope = app.Services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var context = scope.ServiceProvider.GetRequiredService<IConnectionContext>();

        Log.Information("Verifying database connectivity across {Count} configured nodes...", connections.Count);

        foreach (var conn in connections)
        {
            if (conn == null) continue;

            var connectionTargetName = !string.IsNullOrWhiteSpace(conn.Name) ? conn.Name : "PrimaryMySql";

            using (context.UseConnection(connectionTargetName))
            {
                // Safely create and dispose the open connection instance asynchronously
                await using var db = await connectionFactory.CreateConnectionAsync();
                Log.Information("  ✓ {Name} ({Provider}) — Connection established successfully.",
                    connectionTargetName, conn.Provider);
            }
        }

        Log.Information("All database connectivity verifications passed.");
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "FATAL STARTUP FAILURE: One or more target databases are unreachable.");
        throw;
    }


    // ═══════════════════════════════════════════════════════════════════
    // Scalar API Reference (Development only)
    // ═══════════════════════════════════════════════════════════════════
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.WithTitle("DataEngine Core v2 Portal")
                   .WithTheme(ScalarTheme.DeepSpace)
                   .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
        });
    }

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers().RequireRateLimiting("api");
    app.MapHealthChecks("/health");

    await app.RunAsync();
}
catch (Exception ex) when (ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "Application terminated unexpectedly during lifecycle instantiation.");
}
finally
{
    Log.Information("DataEngine API Host cleanly shut down. Flushing log streams...");
    await Log.CloseAndFlushAsync();
}

// ═══════════════════════════════════════════════════════════════════
// HELPER: Parse connections from IConfiguration (UPDATED)
// ═══════════════════════════════════════════════════════════════════

static List<DatabaseOptions> ParseConnectionsFromConfiguration(IConfiguration config)
{
    var connections = new List<DatabaseOptions>();

    // FIXED: Changed from "ConnectionStrings:Named" to match your actual JSON root key "DatabaseSettings:Named"
    var namedSection = config.GetSection("DatabaseSettings:Named");
    if (namedSection.Exists())
    {
        foreach (var child in namedSection.GetChildren())
        {
            var providerStr = child["Provider"] ?? "MySQL";
            if (!Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider))
            {
                Log.Warning("Unknown provider '{Provider}' for connection '{Name}'. Defaulting to MySQL.", providerStr, child.Key);
                provider = DatabaseProvider.MySQL;
            }

            connections.Add(new DatabaseOptions
            {
                Name = child.Key,
                ConnectionString = child["ConnectionString"] ?? string.Empty,
                Provider = provider,
                IsDefault = bool.TryParse(child["IsDefault"], out var isDefault) && isDefault,
                MaxPageSize = int.TryParse(child["MaxPageSize"], out var mps) ? mps : 1000,
                EnableDirectQueryExecution = bool.TryParse(child["EnableDirectQueryExecution"], out var edq) && edq,
                MaxDirectQueryLength = int.TryParse(child["MaxDirectQueryLength"], out var mdql) ? mdql : 5000
            });
        }
    }

    // Pattern 2: Legacy single connection fallback
    if (connections.Count == 0)
    {
        var legacyConnection = config.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(legacyConnection))
        {
            var providerStr = config["ConnectionStrings:Provider"] ?? "MySQL";
            Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider);

            connections.Add(new DatabaseOptions
            {
                Name = "Default",
                ConnectionString = legacyConnection,
                Provider = provider,
                IsDefault = true,
                MaxPageSize = 1000
            });
        }
    }

    // Pattern 3: Legacy array fallback
    var legacyArray = config.GetSection("ConnectionStrings:DefaultConnection").Get<List<string>>();
    if (legacyArray != null && legacyArray.Count > 0 && connections.Count == 0)
    {
        var providerStr = config["ConnectionStrings:Provider"] ?? "MySQL";
        Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider);

        for (int i = 0; i < legacyArray.Count; i++)
        {
            connections.Add(new DatabaseOptions
            {
                Name = $"Default-{i + 1}",
                ConnectionString = legacyArray[i],
                Provider = provider,
                IsDefault = i == 0,
                MaxPageSize = 1000
            });
        }
    }

    return connections;
}
