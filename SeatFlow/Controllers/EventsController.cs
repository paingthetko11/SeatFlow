using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/events")]
public sealed class EventsController(SeatFlowDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<EventSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EventSummaryResponse>>> GetEvents(CancellationToken cancellationToken)
    {
        var events = await dbContext.Events
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new EventSummaryResponse(
                x.EventId,
                x.Name,
                x.Description,
                x.Category,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(events);
    }

    [HttpGet("{eventId:int}/shows")]
    [ProducesResponseType(typeof(IReadOnlyList<ShowSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ShowSummaryResponse>>> GetShowsForEvent(
        int eventId,
        CancellationToken cancellationToken)
    {
        var eventExists = await dbContext.Events
            .AsNoTracking()
            .AnyAsync(x => x.EventId == eventId, cancellationToken);

        if (!eventExists)
        {
            return NotFound(new { message = $"Event {eventId} was not found." });
        }

        var shows = await dbContext.Shows
            .AsNoTracking()
            .Where(x => x.EventId == eventId)
            .OrderBy(x => x.StartsAtUtc)
            .Select(x => new ShowSummaryResponse(
                x.ShowId,
                x.StartsAtUtc,
                x.EndsAtUtc,
                x.Status,
                x.VenueId,
                x.Venue.Name,
                x.ShowSeats.Count(seat => seat.Status == "Available")))
            .ToListAsync(cancellationToken);

        return Ok(shows);
    }
}

public sealed record EventSummaryResponse(
    int EventId,
    string Name,
    string? Description,
    string? Category,
    DateTime CreatedAtUtc);

public sealed record ShowSummaryResponse(
    int ShowId,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    string Status,
    int VenueId,
    string VenueName,
    int AvailableSeatCount);
