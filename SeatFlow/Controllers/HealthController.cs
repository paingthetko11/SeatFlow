using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly SeatFlowDbContext _dbContext;

    public HealthController(SeatFlowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

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

    [HttpGet("database")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDatabaseStatus(CancellationToken cancellationToken)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
        if (!canConnect)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "Unavailable",
                dependency = "SQL Server"
            });
        }

        return Ok(new
        {
            status = "Connected",
            database = _dbContext.Database.GetDbConnection().Database,
            timestampUtc = DateTime.UtcNow
        });
    }
}
