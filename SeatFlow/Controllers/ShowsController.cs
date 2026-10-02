using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/shows")]
public sealed class ShowsController(SeatFlowDbContext dbContext) : ControllerBase
{
    [HttpGet("{showId:int}/seats")]
    [ProducesResponseType(typeof(IReadOnlyList<ShowSeatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ShowSeatResponse>>> GetSeats(
        int showId,
        CancellationToken cancellationToken)
    {
        var showExists = await dbContext.Shows
            .AsNoTracking()
            .AnyAsync(x => x.ShowId == showId, cancellationToken);

        if (!showExists)
        {
            return NotFound(new { message = $"Show {showId} was not found." });
        }

        var seats = await dbContext.ShowSeats
            .AsNoTracking()
            .Where(x => x.ShowId == showId)
            .OrderBy(x => x.Seat.Section)
            .ThenBy(x => x.Seat.RowLabel)
            .ThenBy(x => x.Seat.SeatNumber)
            .Select(x => new ShowSeatResponse(
                x.SeatId,
                x.Seat.Section,
                x.Seat.RowLabel,
                x.Seat.SeatNumber,
                x.Price,
                x.Status,
                x.HoldExpiresAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(seats);
    }
}

public sealed record ShowSeatResponse(
    int SeatId,
    string Section,
    string RowLabel,
    int SeatNumber,
    decimal Price,
    string Status,
    DateTime? HoldExpiresAtUtc);
