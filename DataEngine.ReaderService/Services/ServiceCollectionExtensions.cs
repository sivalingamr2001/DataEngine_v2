using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using DataEngine.ReaderService.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// Composition helper mapping operational interface abstraction tokens to concrete libraries.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Loads core routing factory architectures, validation engines, and data repositories into the pipeline container.
    /// </summary>
    public static IServiceCollection AddDataEngineCore(this IServiceCollection services, DatabaseConfig databaseConfig)
    {
        services.AddSingleton(databaseConfig);
        services.AddSingleton<DatabaseConnectionFactory>();
        services.AddSingleton<ISqlGuardian, SqlGuardian>();
        services.AddScoped<IQueryRepository, QueryRepository>();

        if (databaseConfig.Provider == DatabaseProvider.MySQL)
        {
            services.AddScoped<IGetDataService, MySqlGetDataService>();
        }
        else if (databaseConfig.Provider == DatabaseProvider.Oracle)
        {
            services.AddScoped<IGetDataService, OracleGetDataService>();
        }
        else
        {
            throw new NotSupportedException($"The database provider '{databaseConfig.Provider}' is not supported.");
        }

        return services;
    }
}
