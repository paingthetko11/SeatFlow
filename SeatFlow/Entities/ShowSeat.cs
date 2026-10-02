namespace SeatFlow.Entities;

public sealed class ShowSeat
{
    public int ShowId { get; set; }
    public int SeatId { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "Available";
    public Guid? HoldToken { get; set; }
    public DateTime? HoldExpiresAtUtc { get; set; }
    public Guid? BookingId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Show Show { get; set; } = null!;
    public Seat Seat { get; set; } = null!;
    public Booking? Booking { get; set; }
    public ICollection<BookingSeat> BookingSeats { get; set; } = new List<BookingSeat>();
}
