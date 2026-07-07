using DataEngine.Core.Interfaces;

namespace DataEngine.Core.Services;

/// <summary>
/// AsyncLocal-based connection context for provider resolution.
/// Thread-safe and flows across async calls.
/// </summary>
public sealed class ConnectionContext : IConnectionContext
{
    private static readonly AsyncLocal<string?> _currentConnection = new();

    public string? TargetConnectionName => _currentConnection.Value;

    public IDisposable UseConnection(string name)
    {
        var previous = _currentConnection.Value;
        _currentConnection.Value = name;

        return new ConnectionScope(previous);
    }

    private sealed class ConnectionScope(string? previousValue) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _currentConnection.Value = previousValue;
        }
    }
}