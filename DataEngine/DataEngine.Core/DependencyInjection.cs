using DataEngine.Core.Configuration;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Repositories;
using DataEngine.Core.Security;
using DataEngine.Core.Services;
using DataEngine.Core.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace DataEngine.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddDataEngine(
        this IServiceCollection services,
        Action<DataEngineOptions> configureOptions)
    {
        services.AddMemoryCache();
        services.Configure(configureOptions);

        services.AddScoped<IConnectionContext, ConnectionContext>();
        services.AddHttpContextAccessor();

        services.AddSingleton<IDbProviderStrategy, MySqlProviderStrategy>();
        services.AddSingleton<IDbProviderStrategy, OracleProviderStrategy>();
        services.AddSingleton<IDbProviderStrategyFactory, DbProviderStrategyFactory>();

        services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();

        services.AddSingleton<ISqlGuardian, SqlGuardian>();

        services.AddScoped<IQueryRepository, QueryRepository>();
        services.AddScoped<IFieldMapperRepository, FieldMapperRepository>();

        services.AddScoped<IReaderService, ReaderEngine>();
        services.AddScoped<ITransactionService, TransactionService>();

        services.AddScoped<ITableNameValidator, TableNameValidator>();
        services.AddScoped<IValidationService, ValidationService>();
        services.AddScoped<IUserContext, HttpUserContext>();

        services.AddSingleton<IAuditService, AuditService>();
        services.AddHostedService<AuditBackgroundService>();

        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
