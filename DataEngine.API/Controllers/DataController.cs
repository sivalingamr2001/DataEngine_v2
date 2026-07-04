using DataEngine.ReaderService.Domain;
using DataEngine.ReaderService.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DataController(IGetDataService dataService) : ControllerBase
{
    private readonly IGetDataService _dataService = dataService;

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

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Client closed the request connection.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server processing error: {ex.Message}");
        }
    }
}
