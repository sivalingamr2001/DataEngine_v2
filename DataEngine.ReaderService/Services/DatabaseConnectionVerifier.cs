using DataEngine.ReaderService.Domain;
using MySqlConnector;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace DataEngine.ReaderService.Services;

public static class DatabaseConnectionVerifier
{
    public static async Task TestConnectionsAsync(DatabaseConfig config, CancellationToken ct = default)
    {
        if (config.ConnectionString == null || config.ConnectionString.Count == 0)
        {
            throw new InvalidOperationException("Database configuration contains no connection strings.");
        }

        foreach (var connString in config.ConnectionString)
        {
            await using DbConnection connection = config.Provider switch
            {
                DatabaseProvider.MySQL => new MySqlConnection(connString),
                DatabaseProvider.Oracle => new OracleConnection(connString),
                _ => throw new NotSupportedException($"Database provider '{config.Provider}' is not supported.")
            };

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
        return Regex.Replace(connectionString, @"(?i)(password|pwd|user id|uid)=\s*[^;]+", "$1=******");
    }
}
