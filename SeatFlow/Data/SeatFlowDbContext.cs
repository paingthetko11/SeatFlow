using Microsoft.EntityFrameworkCore;
using SeatFlow.Entities;

namespace SeatFlow.Data;

public sealed class SeatFlowDbContext(DbContextOptions<SeatFlowDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<ShowSeat> ShowSeats => Set<ShowSeat>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingSeat> BookingSeats => Set<BookingSeat>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Venue>(entity =>
        {
            entity.ToTable("Venues", "dbo");
            entity.HasKey(x => x.VenueId);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Address).HasMaxLength(500);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("Events", "dbo");
            entity.HasKey(x => x.EventId);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.Category).HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<Seat>(entity =>
        {
            entity.ToTable("Seats", "dbo");
            entity.HasKey(x => x.SeatId);
            entity.Property(x => x.Section).HasMaxLength(50).IsRequired();
            entity.Property(x => x.RowLabel).HasMaxLength(10).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(x => new { x.VenueId, x.Section, x.RowLabel, x.SeatNumber }).IsUnique();
            entity.HasOne(x => x.Venue).WithMany(x => x.Seats).HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Show>(entity =>
        {
            entity.ToTable("Shows", "dbo");
            entity.HasKey(x => x.ShowId);
            entity.Property(x => x.StartsAtUtc).HasPrecision(3);
            entity.Property(x => x.EndsAtUtc).HasPrecision(3);
            entity.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Scheduled").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasOne(x => x.Event).WithMany(x => x.Shows).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Venue).WithMany(x => x.Shows).HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ShowSeat>(entity =>
        {
            entity.ToTable("ShowSeats", "dbo");
            entity.HasKey(x => new { x.ShowId, x.SeatId });
            entity.Property(x => x.Price).HasPrecision(10, 2);
            entity.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Available").IsRequired();
            entity.Property(x => x.HoldExpiresAtUtc).HasPrecision(3);
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(x => new { x.ShowId, x.Status }).IncludeProperties(x => new { x.SeatId, x.Price });
            entity.HasIndex(x => x.HoldExpiresAtUtc).HasFilter("[Status] = N'Held'");
            entity.HasOne(x => x.Show).WithMany(x => x.ShowSeats).HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Seat).WithMany(x => x.ShowSeats).HasForeignKey(x => x.SeatId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Booking).WithMany(x => x.HeldShowSeats).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Booking>(entity =>
        {
            entity.ToTable("Bookings", "dbo");
            entity.HasKey(x => x.BookingId);
            entity.Property(x => x.CustomerId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending").IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TotalAmount).HasPrecision(10, 2);
            entity.Property(x => x.Currency).HasColumnType("char(3)").HasDefaultValue("USD").IsRequired();
            entity.Property(x => x.HoldExpiresAtUtc).HasPrecision(3);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.Show).WithMany(x => x.Bookings).HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BookingSeat>(entity =>
        {
            entity.ToTable("BookingSeats", "dbo");
            entity.HasKey(x => new { x.BookingId, x.SeatId });
            entity.Property(x => x.PriceAtBooking).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.ShowId, x.SeatId });
            entity.HasOne(x => x.Booking).WithMany(x => x.BookingSeats).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ShowSeat).WithMany(x => x.BookingSeats).HasForeignKey(x => new { x.ShowId, x.SeatId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments", "dbo");
            entity.HasKey(x => x.PaymentId);
            entity.Property(x => x.Provider).HasMaxLength(50).HasDefaultValue("Simulation").IsRequired();
            entity.Property(x => x.ProviderRef).HasMaxLength(200);
            entity.Property(x => x.Amount).HasPrecision(10, 2);
            entity.Property(x => x.Currency).HasColumnType("char(3)").HasDefaultValue("USD").IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending").IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(x => x.UpdatedAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.HasIndex(x => new { x.BookingId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.Booking).WithMany(x => x.Payments).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages", "dbo");
            entity.HasKey(x => x.OutboxMessageId);
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.OccurredAtUtc).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(x => x.ProcessedAtUtc).HasPrecision(3);
            entity.Property(x => x.RetryCount).HasDefaultValue(0);
            entity.HasIndex(x => x.OccurredAtUtc).HasFilter("[ProcessedAtUtc] IS NULL");
        });
    }
}
