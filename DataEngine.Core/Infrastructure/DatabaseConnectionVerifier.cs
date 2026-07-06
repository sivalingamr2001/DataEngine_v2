using DataEngine.Core.Providers;
using DataEngine.ReaderService.Domain;
using System.Data.Common;

namespace DataEngine.ReaderService.Services;

/// <summary>
/// Pre-flight boot probe scanning node cluster accessibility options before web service activation.
/// </summary>
public static class DatabaseConnectionVerifier
{
    /// <summary>
    /// Asserts availability arrays across all defined deployment nodes.
    /// </summary>
    public static async Task TestConnectionsAsync(DatabaseConfig config, IDbProviderStrategyFactory providerStrategyFactory, CancellationToken ct = default)
    {
        if (config.ConnectionString == null || config.ConnectionString.Count == 0)
        {
            throw new InvalidOperationException("Database configuration contains no connection strings.");
        }

        var strategy = providerStrategyFactory.Get(config.Provider);

        foreach (var connString in config.ConnectionString)
        {
            await using DbConnection connection = strategy.CreateConnection(connString);

            try
            {
                await connection.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to the {config.Provider} database. Connection String: '{MaskConnectionString(connString)}'. Error: {ex.Message}", ex);
            }
        }
    }

    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(connectionString, @"(?i)(password|pwd|user id|uid)=\s*[^;]+", "$1=******");
    }
}
