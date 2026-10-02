using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;
using SeatFlow.Entities;
using SeatFlow.Services;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/bookings")]
public sealed class BookingsController(
    SeatFlowDbContext dbContext,
    ExpiredHoldCleanup expiredHoldCleanup) : ControllerBase
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);
    private const int MaximumSeatsPerBooking = 10;

    [HttpPost("hold")]
    [ProducesResponseType(typeof(BookingHoldResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingHoldResponse>> HoldSeats(
        [FromBody] HoldSeatsRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var customerId = request.CustomerId?.Trim();
        var seatIds = request.SeatIds?.Distinct().Order().ToArray() ?? [];

        if (string.IsNullOrWhiteSpace(customerId) || customerId.Length > 100)
        {
            return BadRequest(new { message = "CustomerId must contain 1 to 100 characters." });
        }

        if (seatIds.Length == 0 || seatIds.Length > MaximumSeatsPerBooking)
        {
            return BadRequest(new { message = $"Select between 1 and {MaximumSeatsPerBooking} seats." });
        }

        if (seatIds.Any(x => x <= 0) || request.SeatIds!.Length != seatIds.Length)
        {
            return BadRequest(new { message = "SeatIds must be positive and must not contain duplicates." });
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return BadRequest(new { message = "The Idempotency-Key header must contain 1 to 100 characters." });
        }

        var existingBooking = await FindByIdempotencyKeyAsync(customerId, idempotencyKey, cancellationToken);
        if (existingBooking is not null)
        {
            return await MatchesRequestAsync(existingBooking, request.ShowId, seatIds, cancellationToken)
                ? Ok(await ToResponseAsync(existingBooking, cancellationToken))
                : Conflict(new { message = "This Idempotency-Key was already used for a different booking request." });
        }

        var now = DateTime.UtcNow;
        var bookingId = Guid.NewGuid();
        var holdExpiresAtUtc = now.Add(HoldDuration);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await expiredHoldCleanup.CleanupAsync(cancellationToken);

            var showExists = await dbContext.Shows
                .AsNoTracking()
                .AnyAsync(x => x.ShowId == request.ShowId, cancellationToken);
            if (!showExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound(new { message = $"Show {request.ShowId} was not found." });
            }

            var existingSeatIds = await dbContext.ShowSeats
                .AsNoTracking()
                .Where(x => x.ShowId == request.ShowId && seatIds.Contains(x.SeatId))
                .Select(x => x.SeatId)
                .ToListAsync(cancellationToken);
            if (existingSeatIds.Count != seatIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound(new { message = "One or more seats do not exist for this show." });
            }

            var booking = new Booking
            {
                BookingId = bookingId,
                CustomerId = customerId,
                ShowId = request.ShowId,
                Status = "Pending",
                IdempotencyKey = idempotencyKey,
                TotalAmount = 0,
                Currency = "USD",
                HoldExpiresAtUtc = holdExpiresAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            dbContext.Bookings.Add(booking);
            await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var seatId in seatIds)
            {
                var updatedRows = await dbContext.ShowSeats
                    .Where(x => x.ShowId == request.ShowId
                        && x.SeatId == seatId
                        && (x.Status == "Available"
                            || (x.Status == "Held" && x.HoldExpiresAtUtc <= now)))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, "Held")
                        .SetProperty(x => x.HoldToken, bookingId)
                        .SetProperty(x => x.HoldExpiresAtUtc, holdExpiresAtUtc)
                        .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

                if (updatedRows != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new { message = $"Seat {seatId} is no longer available." });
                }
            }

            var seatPrices = await dbContext.ShowSeats
                .AsNoTracking()
                .Where(x => x.ShowId == request.ShowId && seatIds.Contains(x.SeatId))
                .Select(x => new { x.SeatId, x.Price })
                .ToListAsync(cancellationToken);

            booking.TotalAmount = seatPrices.Sum(x => x.Price);
            booking.UpdatedAtUtc = DateTime.UtcNow;
            dbContext.BookingSeats.AddRange(seatPrices.Select(x => new BookingSeat
            {
                BookingId = bookingId,
                ShowId = request.ShowId,
                SeatId = x.SeatId,
                PriceAtBooking = x.Price
            }));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var response = await ToResponseAsync(booking, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var concurrentBooking = await FindByIdempotencyKeyAsync(customerId, idempotencyKey, cancellationToken);
            if (concurrentBooking is not null)
            {
                return await MatchesRequestAsync(concurrentBooking, request.ShowId, seatIds, cancellationToken)
                    ? Ok(await ToResponseAsync(concurrentBooking, cancellationToken))
                    : Conflict(new { message = "This Idempotency-Key was already used for a different booking request." });
            }

            throw;
        }
    }

    private Task<Booking?> FindByIdempotencyKeyAsync(
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        dbContext.Bookings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);

    private async Task<bool> MatchesRequestAsync(
        Booking booking,
        int showId,
        int[] seatIds,
        CancellationToken cancellationToken)
    {
        if (booking.ShowId != showId)
        {
            return false;
        }

        var existingSeatIds = await dbContext.BookingSeats
            .AsNoTracking()
            .Where(x => x.BookingId == booking.BookingId)
            .Select(x => x.SeatId)
            .Order()
            .ToArrayAsync(cancellationToken);

        return existingSeatIds.SequenceEqual(seatIds);
    }

    private async Task<BookingHoldResponse> ToResponseAsync(
        Booking booking,
        CancellationToken cancellationToken)
    {
        var seats = await dbContext.BookingSeats
            .AsNoTracking()
            .Where(x => x.BookingId == booking.BookingId)
            .OrderBy(x => x.SeatId)
            .Select(x => new BookingHoldSeatResponse(
                x.SeatId,
                x.ShowSeat.Seat.Section,
                x.ShowSeat.Seat.RowLabel,
                x.ShowSeat.Seat.SeatNumber,
                x.PriceAtBooking))
            .ToListAsync(cancellationToken);

        return new BookingHoldResponse(
            booking.BookingId,
            booking.ShowId,
            booking.Status,
            seats,
            booking.TotalAmount,
            booking.Currency,
            booking.HoldExpiresAtUtc,
            booking.CreatedAtUtc);
    }
}

public sealed class HoldSeatsRequest
{
    [Required]
    public int ShowId { get; init; }

    [Required]
    public string CustomerId { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    public int[] SeatIds { get; init; } = [];
}

public sealed record BookingHoldSeatResponse(
    int SeatId,
    string Section,
    string RowLabel,
    int SeatNumber,
    decimal Price);

public sealed record BookingHoldResponse(
    Guid BookingId,
    int ShowId,
    string Status,
    IReadOnlyList<BookingHoldSeatResponse> Seats,
    decimal TotalAmount,
    string Currency,
    DateTime HoldExpiresAtUtc,
    DateTime CreatedAtUtc);
