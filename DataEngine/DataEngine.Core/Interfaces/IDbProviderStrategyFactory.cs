using DataEngine.Core.Enums;

namespace DataEngine.Core.Interfaces;

public interface IDbProviderStrategyFactory
{
    IDbProviderStrategy Get(DatabaseProvider provider);
}