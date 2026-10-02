namespace SeatFlow.Entities;

public sealed class BookingSeat
{
    public Guid BookingId { get; set; }
    public int ShowId { get; set; }
    public int SeatId { get; set; }
    public decimal PriceAtBooking { get; set; }

    public Booking Booking { get; set; } = null!;
    public ShowSeat ShowSeat { get; set; } = null!;
}
