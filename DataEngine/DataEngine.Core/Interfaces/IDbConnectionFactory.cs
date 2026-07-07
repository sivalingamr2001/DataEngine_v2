using System.Data.Common;

namespace DataEngine.Core.Interfaces;

/// <summary>
/// Creates database connections based on resolved provider context.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Creates a connection for the currently resolved provider.
    /// </summary>
    ValueTask<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a connection for a specific named configuration.
    /// </summary>
    ValueTask<DbConnection> CreateConnectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the strategy for the currently resolved provider.
    /// </summary>
    IDbProviderStrategy GetCurrentStrategy();

    /// <summary>
    /// Gets the options for the currently resolved provider.
    /// </summary>
    Configuration.DatabaseOptions GetCurrentOptions();
}