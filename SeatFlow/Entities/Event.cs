namespace SeatFlow.Entities;

public sealed class Event
{
    public int EventId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<Show> Shows { get; set; } = new List<Show>();
}
