using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using DataEngine.ReaderService.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataEngine.ReaderService;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataEngineCore(this IServiceCollection services, DatabaseConfig databaseConfig)
    {
        services.AddSingleton(databaseConfig);
        services.AddScoped<IGetDataService, GetDataService>();
        return services;
    }
}
