using System.Collections.Frozen;
using DataEngine.Core.Enums;
using DataEngine.Core.Interfaces;

namespace DataEngine.Core.Strategies;

/// <summary>
/// Thread-safe factory for database provider strategies.
/// Uses FrozenDictionary for O(1) lookups with zero allocation.
/// </summary>
public sealed class DbProviderStrategyFactory : IDbProviderStrategyFactory
{
    private FrozenDictionary<DatabaseProvider, IDbProviderStrategy> _strategies;

    public DbProviderStrategyFactory(IEnumerable<IDbProviderStrategy> strategies)
    {
        _strategies = strategies
            .ToDictionary(s => s.Provider)
            .ToFrozenDictionary();
    }

    public IDbProviderStrategy Get(DatabaseProvider provider)
    {
        if (_strategies.TryGetValue(provider, out var strategy))
            return strategy;

        throw new NotSupportedException(
            $"No strategy registered for provider '{provider}'. " +
            $"Available: {string.Join(", ", _strategies.Keys)}");
    }

    public IReadOnlyDictionary<DatabaseProvider, IDbProviderStrategy> GetAll() => _strategies;

    public void Register(IDbProviderStrategy strategy)
    {
        // Thread-safe update: create new frozen dictionary
        var builder = new Dictionary<DatabaseProvider, IDbProviderStrategy>(_strategies)
        {
            [strategy.Provider] = strategy
        };
        Interlocked.Exchange(ref _strategies, builder.ToFrozenDictionary());
    }
}