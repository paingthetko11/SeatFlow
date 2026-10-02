namespace SeatFlow.Entities;

public sealed class Seat
{
    public int SeatId { get; set; }
    public int VenueId { get; set; }
    public string Section { get; set; } = string.Empty;
    public string RowLabel { get; set; } = string.Empty;
    public int SeatNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Venue Venue { get; set; } = null!;
    public ICollection<ShowSeat> ShowSeats { get; set; } = new List<ShowSeat>();
}
