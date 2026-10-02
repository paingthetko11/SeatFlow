namespace SeatFlow.Entities;

public sealed class Venue
{
    public int VenueId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();
    public ICollection<Show> Shows { get; set; } = new List<Show>();
}
