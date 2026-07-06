using DataEngine.ReaderService.Domain;

namespace DataEngine.Core.Providers;

public interface IDbProviderStrategyFactory
{
    IDbProviderStrategy Get(DatabaseProvider provider);
}
