using Microsoft.AspNetCore.Mvc;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "Healthy",
            service = "SeatFlow API",
            timestampUtc = DateTime.UtcNow
        });
    }
}
