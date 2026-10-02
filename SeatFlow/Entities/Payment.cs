namespace SeatFlow.Entities;

public sealed class Payment
{
    public Guid PaymentId { get; set; }
    public Guid BookingId { get; set; }
    public string Provider { get; set; } = "Simulation";
    public string? ProviderRef { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Status { get; set; } = "Pending";
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Booking Booking { get; set; } = null!;
}
