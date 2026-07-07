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
public sealed class DataController(IReaderService readerService) : ControllerBase
{
    private readonly IReaderService _readerService = readerService ?? throw new ArgumentNullException(nameof(readerService));

    [HttpPost("fetch")]
    public async Task<IActionResult> FetchData([FromBody] FetchConfig query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            var result = await _readerService.ExecuteAsync(query, ct);
            return Ok(result);
        }
        catch (SqlValidationException valEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = valEx.Message });
        }
        catch (ConfigurationException cfgEx)
        {
            return BadRequest(new { message = cfgEx.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Client closed the request connection.");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Internal engine processing failure.",
                details = SqlErrorTranslator.ToSafeMessage(ex)
            });
        }
    }
}
