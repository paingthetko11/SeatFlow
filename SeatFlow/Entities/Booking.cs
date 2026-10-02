namespace SeatFlow.Entities;

public sealed class Booking
{
    public Guid BookingId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int ShowId { get; set; }
    public string Status { get; set; } = "Pending";
    public string IdempotencyKey { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime HoldExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Show Show { get; set; } = null!;
    public ICollection<ShowSeat> HeldShowSeats { get; set; } = new List<ShowSeat>();
    public ICollection<BookingSeat> BookingSeats { get; set; } = new List<BookingSeat>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
