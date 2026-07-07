using MySqlConnector;
using Oracle.ManagedDataAccess.Client;

namespace DataEngine.Core.Resilience;

public static class SqlErrorTranslator
{
    public static string ToSafeMessage(Exception ex) => ex switch
    {
        MySqlException { Number: 1062 } => "A record with this value already exists.",
        MySqlException { Number: 1451 or 1452 } => "This record is referenced by other data and cannot be modified or deleted.",
        MySqlException { Number: 1213 } => "The operation was blocked by concurrent activity. Please retry.",
        MySqlException { Number: 1205 } => "The operation timed out waiting for a lock. Please retry.",
        MySqlException { Number: 1264 } => "A value is out of range for its column.",
        MySqlException { Number: 1406 } => "A value is too long for its column.",
        MySqlException => "A database error occurred while processing the request.",

        OracleException { Number: 1 } => "A record with this value already exists.",
        OracleException { Number: 2291 or 2292 } => "This record is referenced by other data and cannot be modified or deleted.",
        OracleException { Number: 60 } => "The operation was blocked by concurrent activity. Please retry.",
        OracleException { Number: 54 or 30006 } => "The operation timed out waiting for a lock. Please retry.",
        OracleException { Number: 12899 or 1438 } => "A value is too long or out of range for its column.",
        OracleException => "A database error occurred while processing the request.",

        OperationCanceledException => "The operation was cancelled or timed out.",
        _ => "An unexpected error occurred while processing the request."
    };
}
