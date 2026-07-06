using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataEngine.API.Controllers;

/// <summary>
/// Operational API gateway exposing endpoints for secure high-throughput data engine extraction pipelines.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DataController(IGetDataService dataService) : ControllerBase
{
    private readonly IGetDataService _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));

    /// <summary>
    /// Processes incoming data fetch configuration models to evaluate safe data access routines.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<IActionResult> FetchData([FromBody] FetchConfig query, CancellationToken ct)
    {
        if (query == null)
        {
            return BadRequest("Query configuration cannot be null.");
        }

        try
        {
            FetchResult<Dictionary<string, object?>> result = await _dataService.ExecuteAsync(query, ct);

            if (!result.Success)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Client closed the request connection.");
        }
        catch (Exception)
        {
            return StatusCode(500, "Internal server processing error.");
        }
    }
}
