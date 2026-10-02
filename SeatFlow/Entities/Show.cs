namespace SeatFlow.Entities;

public sealed class Show
{
    public int ShowId { get; set; }
    public int EventId { get; set; }
    public int VenueId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public string Status { get; set; } = "Scheduled";
    public DateTime CreatedAtUtc { get; set; }

    public Event Event { get; set; } = null!;
    public Venue Venue { get; set; } = null!;
    public ICollection<ShowSeat> ShowSeats { get; set; } = new List<ShowSeat>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
