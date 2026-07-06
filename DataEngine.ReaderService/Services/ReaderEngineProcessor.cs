using DataEngine.ReaderService.Domain;

namespace DataEngine.ReaderService.Services;

public interface IReaderEngineProcessor
{
    void Prepare(FetchConfig query);
}

public sealed class ReaderEngineProcessor : IReaderEngineProcessor
{
    public void Prepare(FetchConfig query)
    {
        if (query == null) return;

        // Operational placeholder for read-specific query sanitization or default criteria
    }
}
