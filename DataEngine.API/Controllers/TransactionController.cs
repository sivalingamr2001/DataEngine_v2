using DataEngine.TransactionService.Domain;
using DataEngine.TransactionService.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransactionController(ITransactionService transactionService) : ControllerBase
{
    private readonly ITransactionService _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteTransaction([FromBody] TransactionRequest request, CancellationToken ct)
    {
        var result = await _transactionService.TransactionAsync(request, ct);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
