using DataEngine.TransactionService.Domain;

namespace DataEngine.TransactionService.Interfaces;

/// <summary>
/// Single entry point for every Create/Update/Delete operation.
/// Dispatches internally to the field-mapper (dynamic) path or the
/// model-binding (strongly-typed POCO) path based on <see cref="TransactionCommand.Mode"/>.
/// </summary>
public interface ITransactionService
{
    Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken ct = default);
}