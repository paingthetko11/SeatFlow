using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;
using SeatFlow.Entities;
using SeatFlow.Services;

namespace SeatFlow.Controllers;

[ApiController]
[Route("api/bookings/{bookingId:guid}/payment")]
public sealed class BookingPaymentsController(
    SeatFlowDbContext dbContext,
    ExpiredHoldCleanup expiredHoldCleanup,
    RedisSeatLockService redisSeatLockService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(PaymentSimulationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<PaymentSimulationResponse>> SimulatePayment(
        Guid bookingId,
        [FromBody] SimulatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request.Succeeded is null)
        {
            return BadRequest(new { message = "Succeeded must be specified." });
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return BadRequest(new { message = "The Idempotency-Key header must contain 1 to 100 characters." });
        }

        await CleanupExpiredHoldsAsync(cancellationToken);

        var succeeded = request.Succeeded.Value;
        var existingPayment = await FindPaymentAsync(bookingId, idempotencyKey, cancellationToken);
        if (existingPayment is not null)
        {
            return existingPayment.Status == (succeeded ? "Succeeded" : "Failed")
                ? Ok(ToResponse(existingPayment))
                : Conflict(new { message = "This Idempotency-Key was already used with a different payment outcome." });
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var booking = await dbContext.Bookings
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.BookingId == bookingId, cancellationToken);
            if (booking is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound(new { message = "Booking was not found." });
            }

            if (booking.Status == "Expired"
                || (booking.Status == "Pending" && booking.HoldExpiresAtUtc <= DateTime.UtcNow))
            {
                await transaction.RollbackAsync(cancellationToken);
                return StatusCode(StatusCodes.Status410Gone, new { message = "The booking hold has expired." });
            }

            if (booking.Status != "Pending")
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(new { message = $"Payment cannot be processed for booking status '{booking.Status}'." });
            }

            var seatIds = await dbContext.BookingSeats
                .AsNoTracking()
                .Where(x => x.BookingId == bookingId)
                .Select(x => x.SeatId)
                .Order()
                .ToArrayAsync(cancellationToken);

            if (seatIds.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(new { message = "The booking has no seats." });
            }

            var now = DateTime.UtcNow;
            var bookingStatus = succeeded ? "Confirmed" : "PaymentFailed";
            var updatedBookings = await dbContext.Bookings
                .Where(x => x.BookingId == bookingId
                    && x.Status == "Pending"
                    && x.HoldExpiresAtUtc > now)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, bookingStatus)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

            if (updatedBookings != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(new { message = "The booking changed while payment was being processed. Retry with a new Idempotency-Key." });
            }

            var showSeats = dbContext.ShowSeats
                .Where(x => x.ShowId == booking.ShowId
                    && seatIds.Contains(x.SeatId)
                    && x.Status == "Held"
                    && x.HoldToken == bookingId);

            if (succeeded)
            {
                var updatedSeats = await showSeats
                    .Where(x => x.HoldExpiresAtUtc > now)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, "Booked")
                        .SetProperty(x => x.BookingId, bookingId)
                        .SetProperty(x => x.HoldToken, (Guid?)null)
                        .SetProperty(x => x.HoldExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

                if (updatedSeats != seatIds.Length)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new { message = "One or more seat holds have expired or changed." });
                }
            }
            else
            {
                await showSeats.ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, "Available")
                    .SetProperty(x => x.HoldToken, (Guid?)null)
                    .SetProperty(x => x.HoldExpiresAtUtc, (DateTime?)null)
                    .SetProperty(x => x.BookingId, (Guid?)null)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            }

            var payment = new Payment
            {
                PaymentId = Guid.NewGuid(),
                BookingId = bookingId,
                Provider = "Simulation",
                ProviderRef = $"SIM-{Guid.NewGuid():N}",
                Amount = booking.TotalAmount,
                Currency = booking.Currency,
                Status = succeeded ? "Succeeded" : "Failed",
                IdempotencyKey = idempotencyKey,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            dbContext.Payments.Add(payment);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await redisSeatLockService.ReleaseManyAsync(booking.ShowId, seatIds, bookingId);

            return Ok(ToResponse(payment));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var concurrentPayment = await FindPaymentAsync(bookingId, idempotencyKey, cancellationToken);
            if (concurrentPayment is not null)
            {
                return concurrentPayment.Status == (succeeded ? "Succeeded" : "Failed")
                    ? Ok(ToResponse(concurrentPayment))
                    : Conflict(new { message = "This Idempotency-Key was already used with a different payment outcome." });
            }

            throw;
        }
    }

    private Task<Payment?> FindPaymentAsync(Guid bookingId, string idempotencyKey, CancellationToken cancellationToken) =>
        dbContext.Payments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.BookingId == bookingId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);

    private async Task CleanupExpiredHoldsAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await expiredHoldCleanup.CleanupAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static PaymentSimulationResponse ToResponse(Payment payment) => new(
        payment.PaymentId,
        payment.BookingId,
        payment.ProviderRef,
        payment.Status,
        payment.Amount,
        payment.Currency,
        payment.CreatedAtUtc);
}

public sealed class SimulatePaymentRequest
{
    [Required]
    public bool? Succeeded { get; init; }
}

public sealed record PaymentSimulationResponse(
    Guid PaymentId,
    Guid BookingId,
    string? ProviderReference,
    string Status,
    decimal Amount,
    string Currency,
    DateTime ProcessedAtUtc);
