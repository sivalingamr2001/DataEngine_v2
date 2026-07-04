using DataEngine.TransactionService.Domain;
using DataEngine.TransactionService.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataEngine.API.Controllers;

/// <summary>
/// Operational API gateway exposing endpoints for secure high-throughput data mutations and transactions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TransactionController(ITransactionService transactionService) : ControllerBase
{
    private readonly ITransactionService _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));

    /// <summary>
    /// Processes incoming multi-level transaction requests containing create, update, or delete commands.
    /// </summary>
    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteTransaction([FromBody] TransactionRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return BadRequest("Transaction request payload cannot be null.");
        }

        var result = await _transactionService.TransactionAsync(request, ct);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
