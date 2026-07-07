namespace DataEngine.Core.Interfaces;

/// <summary>
/// Resolves which database provider to use for the current execution context.
/// Implementations can use AsyncLocal, HTTP headers, JWT claims, or tenant resolution.
/// </summary>
public interface IConnectionContext
{
    /// <summary>
    /// The name of the connection to use. Null = use default.
    /// </summary>
    string? TargetConnectionName { get; }

    /// <summary>
    /// Sets the target connection for the current async flow.
    /// </summary>
    IDisposable UseConnection(string name);
}