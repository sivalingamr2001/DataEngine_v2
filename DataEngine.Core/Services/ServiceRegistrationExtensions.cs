using DataEngine.Core.Auditing;
using DataEngine.Core.Caching;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Providers;
using DataEngine.Core.Security;
using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DataEngine.Core.Services;

/// <summary>
/// Unified architecture composition root managing cross-cutting dependency configuration metrics.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Mounts core engine components, caching vectors, read pipelines, and transaction framework nodes onto the container.
    /// </summary>
    public static IServiceCollection AddDataEngineServices(this IServiceCollection services, DatabaseConfig databaseConfig)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (databaseConfig == null) throw new ArgumentNullException(nameof(databaseConfig));

        // ==========================================
        // 1. GLOBAL INFRASTRUCTURE & CONFIGURATION
        // ==========================================
        services.AddSingleton(databaseConfig);
        services.AddHttpContextAccessor();
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse("localhost:6379");
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            return ConnectionMultiplexer.Connect(options);
        });

        // ==========================================
        // 2. UNIFORM ENGINE PRE-PROCESSORS
        // ==========================================
        services.AddSingleton<ITransactionEngineProcessor, TransactionEngineProcessor>();
        services.AddSingleton<IReaderEngineProcessor, ReaderEngineProcessor>();

        // ==========================================
        // 3. CACHING & OBSERVED SECURITY LAYERS
        // ==========================================
        services.AddSingleton<ITieredCache, TieredCache>();
        services.AddSingleton<ISqlGuardian, SqlGuardian>();
        services.AddSingleton<ITableNameValidator, TableNameValidator>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IValidationService, ValidationService>();

        // ==========================================
        // 4. DATABASE PROVIDER STRATEGY VECTORING
        // ==========================================
        services.AddSingleton<MySqlProviderStrategy>();
        services.AddSingleton<OracleProviderStrategy>();
        services.AddSingleton<IDbProviderStrategyFactory, DbProviderStrategyFactory>();
        services.AddSingleton<DatabaseConnectionFactory>();

        services.AddTransient<IDbProviderStrategy>(sp =>
        {
            return databaseConfig.Provider switch
            {
                DatabaseProvider.MySQL => sp.GetRequiredService<MySqlProviderStrategy>(),
                DatabaseProvider.Oracle => sp.GetRequiredService<OracleProviderStrategy>(),
                _ => throw new NotSupportedException($"The database provider '{databaseConfig.Provider}' is not supported by the composition framework.")
            };
        });

        // ==========================================
        // 5. READ PIPELINE SERVICE CONFIGURATIONS
        // ==========================================
        services.AddScoped<IDataProvider, DatabaseDataProvider>();
        services.AddScoped<IQueryRepository, QueryRepository>();
        services.AddScoped<IGetDataService, SqlGetDataService>();

        // ==========================================
        // 6. MUTATING TRANSACTION FRAMEWORK LAYER
        // ==========================================
        services.AddScoped<IFieldMapperRepository, FieldMapperRepository>();
        services.AddScoped<ITransactionService, TransactionService.Services.TransactionService>();

        return services;
    }
}
