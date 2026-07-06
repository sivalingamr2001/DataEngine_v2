namespace DataEngine.Core.Interfaces;

public interface IDataEngine
{
    IReaderService Reader { get; }
    ITransactionService Transaction { get; };
}