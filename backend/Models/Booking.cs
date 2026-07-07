namespace BookingApi.Models;

public class Booking
{
    public Guid Id { get; set; }

    public Guid ResourceId { get; set; }
    public Resource? Resource { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Start of the booking window, stored in UTC.</summary>
    public DateTime StartDateTime { get; set; }

    /// <summary>End of the booking window, stored in UTC.</summary>
    public DateTime EndDateTime { get; set; }

    /// <summary>Soft-delete flag. Cancelled bookings are ignored for overlap and retrieval.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. SQLite has no native rowversion type, so this is bumped
    /// by a BEFORE UPDATE trigger (see AppDbContext) to make DbUpdateConcurrencyException fire.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
