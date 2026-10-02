using Microsoft.EntityFrameworkCore;
using SeatFlow.Data;

namespace SeatFlow.Services;

public sealed class ExpiredHoldCleanup(SeatFlowDbContext dbContext)
{
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await dbContext.Bookings
            .Where(x => x.Status == "Pending" && x.HoldExpiresAtUtc <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Expired")
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);

        await dbContext.ShowSeats
            .Where(x => x.Status == "Held" && x.HoldExpiresAtUtc <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Available")
                .SetProperty(x => x.HoldToken, (Guid?)null)
                .SetProperty(x => x.HoldExpiresAtUtc, (DateTime?)null)
                .SetProperty(x => x.BookingId, (Guid?)null)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
    }
}
