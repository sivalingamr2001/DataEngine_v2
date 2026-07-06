using DataEngine.ReaderService.Domain;
using System.Collections.Generic;
using System.Linq;

namespace DataEngine.Core.Providers;

public sealed class DbProviderStrategyFactory(IEnumerable<IDbProviderStrategy> strategies) : IDbProviderStrategyFactory
{
    private readonly Dictionary<DatabaseProvider, IDbProviderStrategy> _strategies = strategies.ToDictionary(s => s.Provider);

    public IDbProviderStrategy Get(DatabaseProvider provider)
    {
        if (_strategies.TryGetValue(provider, out var strategy))
        {
            return strategy;
        }

        throw new NotSupportedException($"No database provider strategy is registered for '{provider}'.");
    }
}
