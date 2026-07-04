using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Services;
using DataEngine.TransactionService;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting up DataEngine API Host...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    builder.Services.AddControllers();
    builder.Services.AddOpenApi();

    var provider = Enum.TryParse<DatabaseProvider>(builder.Configuration["ConnectionStrings:Provider"], true, out var parsedProvider)
        ? parsedProvider
        : DatabaseProvider.MySQL;

    var connectionStrings = new List<string>();
    var connectionStringArray = builder.Configuration.GetSection("ConnectionStrings:DefaultConnection").Get<List<string>>();

    if (connectionStringArray != null && connectionStringArray.Count > 0)
    {
        connectionStrings.AddRange(connectionStringArray);
    }
    else
    {
        var singleConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(singleConnectionString))
        {
            connectionStrings.Add(singleConnectionString);
        }
    }

    var databaseConfig = new DatabaseConfig
    {
        ConnectionString = connectionStrings,
        Provider = provider
    };

    builder.Services.AddDataEngineCore(databaseConfig);
    builder.Services.AddDataEngineTransactionFramework();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    try
    {
        Log.Information("Verifying database target accessibility across {Count} nodes...", databaseConfig.ConnectionString.Count);
        await DatabaseConnectionVerifier.TestConnectionsAsync(databaseConfig);
        Log.Information("Database connectivity verification successfully established.");
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "FATAL STARTUP FAILURE: Target database unreachable.");
        throw;
    }

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
    app.UseAuthorization();
    app.MapControllers();

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
