using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;
using SeatFlow.Entities;
using SeatFlow.Services;
using StackExchange.Redis;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/bookings")]
public sealed class BookingsController(
    SeatFlowDbContext dbContext,
    ExpiredHoldCleanup expiredHoldCleanup,
    RedisSeatLockService redisSeatLockService) : ControllerBase
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);
    private const int MaximumSeatsPerBooking = 10;

    [HttpGet("{bookingId:guid}")]
    [ProducesResponseType(typeof(BookingDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDetailsResponse>> GetBooking(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings
            .AsNoTracking()
            .Where(x => x.BookingId == bookingId)
            .Select(x => new BookingDetailsResponse(
                x.BookingId,
                x.CustomerId,
                x.ShowId,
                x.Status,
                x.TotalAmount,
                x.Currency,
                x.HoldExpiresAtUtc,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                x.BookingSeats
                    .OrderBy(seat => seat.SeatId)
                    .Select(seat => new BookingHoldSeatResponse(
                        seat.SeatId,
                        seat.ShowSeat.Seat.Section,
                        seat.ShowSeat.Seat.RowLabel,
                        seat.ShowSeat.Seat.SeatNumber,
                        seat.PriceAtBooking))
                    .ToList(),
                x.Payments
                    .OrderByDescending(payment => payment.CreatedAtUtc)
                    .Select(payment => new BookingPaymentResponse(
                        payment.PaymentId,
                        payment.Status,
                        payment.Amount,
                        payment.Currency,
                        payment.Provider,
                        payment.CreatedAtUtc))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        return booking is null ? NotFound() : Ok(booking);
    }

    [HttpPost("{bookingId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelBooking(Guid bookingId, CancellationToken cancellationToken)
    {
        await CleanupExpiredHoldsAsync(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var updatedBookings = await dbContext.Bookings
            .Where(x => x.BookingId == bookingId && x.Status == "Pending" && x.HoldExpiresAtUtc > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

        if (updatedBookings == 0)
        {
            var currentStatus = await dbContext.Bookings
                .AsNoTracking()
                .Where(x => x.BookingId == bookingId)
                .Select(x => x.Status)
                .SingleOrDefaultAsync(cancellationToken);

            await transaction.RollbackAsync(cancellationToken);

            if (currentStatus is null)
            {
                return NotFound();
            }

            if (currentStatus == "Cancelled")
            {
                return NoContent();
            }

            return Conflict(new { message = $"Booking cannot be cancelled from status '{currentStatus}'." });
        }

        var bookingSeats = await dbContext.BookingSeats
            .AsNoTracking()
            .Where(x => x.BookingId == bookingId)
            .Select(x => new { x.ShowId, x.SeatId })
            .ToListAsync(cancellationToken);

        foreach (var seat in bookingSeats)
        {
            await dbContext.ShowSeats
                .Where(x => x.ShowId == seat.ShowId
                    && x.SeatId == seat.SeatId
                    && x.Status == "Held"
                    && x.HoldToken == bookingId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, "Available")
                    .SetProperty(x => x.HoldToken, (Guid?)null)
                    .SetProperty(x => x.HoldExpiresAtUtc, (DateTime?)null)
                    .SetProperty(x => x.BookingId, (Guid?)null)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var group in bookingSeats.GroupBy(x => x.ShowId))
        {
            await redisSeatLockService.ReleaseManyAsync(
                group.Key,
                group.Select(x => x.SeatId),
                bookingId);
        }

        return NoContent();
    }

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

        await CleanupExpiredHoldsAsync(cancellationToken);

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
        IReadOnlyList<int> acquiredSeatIds = [];
        var databaseCommitted = false;
        try
        {
            RedisSeatLockResult lockResult;
            try
            {
                lockResult = await redisSeatLockService.TryAcquireManyAsync(
                    request.ShowId,
                    seatIds,
                    bookingId,
                    holdExpiresAtUtc,
                    cancellationToken);
            }
            catch (RedisException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    message = "The seat locking service is unavailable. Try again shortly."
                });
            }

            if (!lockResult.Acquired)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(new { message = $"Seat {lockResult.ContendedSeatId} is being held by another request." });
            }

            acquiredSeatIds = lockResult.AcquiredSeatIds;

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
            databaseCommitted = true;

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
        finally
        {
            if (!databaseCommitted && acquiredSeatIds.Count > 0)
            {
                await redisSeatLockService.ReleaseManyAsync(request.ShowId, acquiredSeatIds, bookingId);
            }
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

    private async Task CleanupExpiredHoldsAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await expiredHoldCleanup.CleanupAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

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

public sealed record BookingDetailsResponse(
    Guid BookingId,
    string CustomerId,
    int ShowId,
    string Status,
    decimal TotalAmount,
    string Currency,
    DateTime HoldExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<BookingHoldSeatResponse> Seats,
    IReadOnlyList<BookingPaymentResponse> Payments);

public sealed record BookingPaymentResponse(
    Guid PaymentId,
    string Status,
    decimal Amount,
    string Currency,
    string Provider,
    DateTime CreatedAtUtc);
