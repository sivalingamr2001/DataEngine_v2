using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using DataEngine.ReaderService.Services.DataEngine.ReaderService.Services;
using DataEngine.ReaderService.Services.DataEngine.ReaderService.Services.DataEngine.ReaderService.DataEngine.ReaderService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DataEngine.ReaderService.Services;

public class GetDataService(DatabaseConfig config) : IGetDataService
{
    private readonly DatabaseConfig _config = config;

    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
    {
        throw new NotImplementedException("MySQL query engine execution is not yet implemented.");
    }
}


//To implement an enhanced connection factory with connection multiplexing, read-replica load balancing, and comprehensive monitoring, you can build an advanced wrapper around your[ADO.NET](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/ado-net-overview) providers.
//Since you have a List<string> ConnectionString in your DatabaseConfig, we can treat the first string as the primary master node(for writes/reads) and any subsequent strings as read replicas.
//Here is the production-grade, asynchronous implementation of DatabaseConnectionFactory for your DataEngine.ReaderService project.
//## Step 1: Install a High-Performance Logging Interface
//Make sure your class library has access to standard.NET Core dependency injection and monitoring logging by running this command in your DataEngine.ReaderService directory:

//dotnet add package Microsoft.Extensions.Logging.Abstractions

//------------------------------
//## Step 2: Implement the DatabaseConnectionFactory
//Create this file inside your library's Services folder. It uses a Round-Robin algorithm to distribute traffic across read replicas and manages connection pooling/multiplexing handles safely:

//using System.Data.Common;using System.Diagnostics;using System.Threading.Interlocked;using DataEngine.ReaderService.Domain;using Microsoft.Extensions.Logging;using MySqlConnector;using Oracle.ManagedDataAccess.Client; // Ensure .Core package is referenced
//namespace DataEngine.ReaderService.Services;

//public sealed class DatabaseConnectionFactory
//{
//    private readonly DatabaseConfig _config;
//    private readonly ILogger<DatabaseConnectionFactory> _logger;
//    private long _replicaIndex = 0;

//    public DatabaseConnectionFactory(DatabaseConfig config, ILogger<DatabaseConnectionFactory> logger)
//    {
//        _config = config ?? throw new ArgumentNullException(nameof(config));
//        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//    }

//    /// <summary>
//    /// Gets a connection to the Primary (Master) Database Node.
//    /// </summary>
//    public Task<DbConnection> CreatePrimaryConnectionAsync(CancellationToken ct = default)
//    {
//        if (_config.ConnectionString == null || _config.ConnectionString.Count == 0)
//        {
//            throw new InvalidOperationException("No connection strings available in configuration.");
//        }

//        // The first connection string is always considered the Master/Primary node
//        return CreateAndOpenConnectionAsync(_config.ConnectionString[0], "Primary-Master", ct);
//    }

//    /// <summary>
//    /// Gets an optimized, load-balanced connection from available Read Replicas.
//    /// Falls back to the Primary node if no separate replicas are registered.
//    /// </summary>
//    public Task<DbConnection> CreateReadReplicaConnectionAsync(CancellationToken ct = default)
//    {
//        if (_config.ConnectionString == null || _config.ConnectionString.Count == 0)
//        {
//            throw new InvalidOperationException("No connection strings available in configuration.");
//        }

//        // If only 1 connection string exists, route read requests to the Primary node
//        if (_config.ConnectionString.Count == 1)
//        {
//            return CreatePrimaryConnectionAsync(ct);
//        }

//        // Round-robin selection across the remaining replica connection strings
//        int replicaCount = _config.ConnectionString.Count - 1;
//        long index = Interlocked.Increment(ref _replicaIndex);
//        int selectedIndex = 1 + (int)(index % replicaCount);

//        string selectedConnectionString = _config.ConnectionString[selectedIndex];
//        return CreateAndOpenConnectionAsync(selectedConnectionString, $"Read-Replica-Node-{selectedIndex}", ct);
//    }

//    private async Task<DbConnection> CreateAndOpenConnectionAsync(string connectionString, string nodeLabel, CancellationToken ct)
//    {
//        // 1. Connection Multiplexing Handling
//        // .NET 

//        ADO.NET

//         connection pools handle physical multiplexing automatically
//                // under the hood if the connection string parameters match perfectly.
//                DbConnection connection = _config.Provider switch
//                {
//                    DatabaseProvider.MySQL => new MySqlConnection(connectionString),
//                    DatabaseProvider.Oracle => new OracleConnection(connectionString),
//                    _ => throw new NotSupportedException($"Provider {_config.Provider} is not supported.")
//                };

//        // 2. Comprehensive Monitoring Framework Integration
//        var stopwatch = Stopwatch.StartNew();
//        _logger.LogDebug("Opening database connection context for target node: {NodeLabel}...", nodeLabel);

//        try
//        {
//            await connection.OpenAsync(ct);
//            stopwatch.Stop();

//            _logger.LogInformation(
//                "Database connection established successfully. Node: {NodeLabel} | Provider: {Provider} | Handshake Latency: {ElapsedMs}ms",
//                nodeLabel, _config.Provider, stopwatch.ElapsedMilliseconds);

//            return connection;
//        }
//        catch (Exception ex)
//        {
//            stopwatch.Stop();
//            _logger.LogError(ex,
//                "CRITICAL: Database connection establishment failed. Node: {NodeLabel} | Latency: {ElapsedMs}ms | Error: {Message}",
//                nodeLabel, stopwatch.ElapsedMilliseconds, ex.Message);

//            await connection.DisposeAsync();
//            throw;
//        }
//    }
//}

//------------------------------
//## Step 3: Register It in ServiceCollectionExtensions.cs
//Update your DI extension class to register this advanced factory block as a Singleton, allowing it to cleanly track the round-robin replica distribution index across concurrent threads:

//using DataEngine.ReaderService.Domain;using DataEngine.ReaderService.Interfaces;using DataEngine.ReaderService.Services;using Microsoft.Extensions.DependencyInjection;
//namespace DataEngine.ReaderService;

//public static class ServiceCollectionExtensions
//{
//    public static IServiceCollection AddDataEngineCore(this IServiceCollection services, DatabaseConfig databaseConfig)
//    {
//        services.AddSingleton(databaseConfig);

//        // Register your newly enhanced tracking connection factory
//        services.AddSingleton<DatabaseConnectionFactory>();

//        if (databaseConfig.Provider == DatabaseProvider.MySQL)
//        {
//            services.AddScoped<IGetDataService, MySqlGetDataService>();
//        }
//        else if (databaseConfig.Provider == DatabaseProvider.Oracle)
//        {
//            services.AddScoped<IGetDataService, OracleGetDataService>();
//        }

//        return services;
//    }
//}

//------------------------------
//## Step 4: Consume It Inside Your Service (Example)
//Now your specific database providers(like MySqlGetDataService) can ingest this factory.It calls CreateReadReplicaConnectionAsync() to instantly offload heavy SELECT queries onto your read nodes:

//using DataEngine.ReaderService.Domain;using DataEngine.ReaderService.Interfaces;using System.Data.Common;
//namespace DataEngine.ReaderService.Services;

//public class MySqlGetDataService : IGetDataService
//{
//    private readonly DatabaseConnectionFactory _connectionFactory;

//    public MySqlGetDataService(DatabaseConnectionFactory connectionFactory)
//    {
//        _connectionFactory = connectionFactory;
//    }

//    public async Task<FetchResult<Dictionary<string, object?>>> ExecuteAsync(FetchConfig query, CancellationToken ct)
//    {
//        // Offloads the SELECT operation onto an available read-replica node instantly
//        await using DbConnection connection = await _connectionFactory.CreateReadReplicaConnectionAsync(ct);

//        // Execute your query logic here using the open connection object...
//        throw new NotImplementedException();
//    }
//}

//To finalize the service execution layer, let me know:

//* Do you want to include automatic retry-policies(using MaxRetryCount and RetryDelayMs from your config) inside the factory when a node fails to open?
//* Are you planning to add a performance alert metric threshold that logs a warning if a database handshake takes longer than a certain number of milliseconds?


