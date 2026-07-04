using DataEngine.TransactionService.Interfaces;
using DataEngine.TransactionService.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace DataEngine.TransactionService;

/// <summary>
/// Composition registration utilities loading transaction layers into microservice containers.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Mounts transaction service engines and dynamic field mapping definitions onto DI collection vectors.
    /// </summary>
    public static IServiceCollection AddDataEngineTransactionFramework(this IServiceCollection services)
    {
        services.AddScoped<IFieldMapperRepository, FieldMapperRepository>();
        services.AddScoped<ITransactionService, Services.TransactionService>();
        return services;
    }
}
