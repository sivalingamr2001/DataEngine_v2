using System;
using System.Threading;
using System.Threading.Tasks;
using DataEngine.Core.Domain;
using DataEngine.Core.Exceptions;
using DataEngine.Core.Interfaces;
using DataEngine.Core.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DataEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TransactionController(
    ITransactionService transactionService,
    IUserContext userContext) : ControllerBase
{
    private readonly ITransactionService _transactionService =
        transactionService ?? throw new ArgumentNullException(nameof(transactionService));

    private readonly IUserContext _userContext =
        userContext ?? throw new ArgumentNullException(nameof(userContext));

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteTransaction([FromBody] TransactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Request.Headers.TryGetValue("x-correlation-id", out var correlationId)
                ? correlationId.ToString()
                : request.EffectiveTransactionId
            : request.CorrelationId;

        request.IpAddress ??= HttpContext.Connection.RemoteIpAddress?.ToString();

        if (!string.IsNullOrWhiteSpace(_userContext.UserId))
            request.UserId = _userContext.UserId;

        try
        {
            var result = await _transactionService.TransactionAsync(request, ct);

            if (!result.Success)
                return BadRequest(result);

            return Ok(result);
        }
        catch (SqlValidationException valEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Security policy rejection.", details = valEx.Message });
        }
        catch (UnauthorizedAccessException authEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = authEx.Message });
        }
        catch (ConfigurationException cfgEx)
        {
            return BadRequest(new { message = "Invalid transaction parameters.", details = cfgEx.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "The client cancelled the transactional operations thread pipeline.");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "A structural driver or database error crashed the active transaction state thread.",
                details = SqlErrorTranslator.ToSafeMessage(ex)
            });
        }
    }
}
