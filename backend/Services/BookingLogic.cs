using BookingApi.Models;

namespace BookingApi.Services;

/// <summary>
/// Pure, dependency-free booking rules. Kept separate from the database so the
/// core logic (overlap detection, validation) is directly unit-testable.
/// </summary>
public static class BookingLogic
{
    /// <summary>
    /// Minimum gap required between two DIFFERENT users' bookings for the same resource — a turnover
    /// buffer. A booking ending at 10:00 frees the resource for another user at 10:01, not at 10:00.
    /// This is a mandatory product rule: it is never zero for a foreign booking. The same user may
    /// book back-to-back against their own bookings (see <see cref="HasConflict"/>).
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Bookings are half-open intervals [start, end). Two of them conflict unless they are separated
    /// by at least <paramref name="buffer"/>: i.e. iff newStart &lt; existingEnd + buffer AND
    /// newEnd + buffer &gt; existingStart. With buffer = zero this is plain interval overlap, so a
    /// booking ending at 10:00 and one starting at 10:00 do NOT conflict; with the default 1-minute
    /// buffer, the next booking may start at 10:01.
    /// </summary>
    public static bool Overlaps(DateTime newStart, DateTime newEnd, DateTime existingStart, DateTime existingEnd, TimeSpan buffer)
        => newStart < existingEnd + buffer && newEnd + buffer > existingStart;

    /// <summary>
    /// Returns true if the proposed window collides with any ACTIVE existing booking for the same
    /// resource. The mandatory turnover <paramref name="buffer"/> is enforced against OTHER users'
    /// bookings; a booking owned by <paramref name="newUserId"/> uses a zero buffer, so a user may
    /// book back-to-back against their own booking (genuine overlap is still rejected). Cancelled
    /// bookings are ignored.
    /// </summary>
    public static bool HasConflict(DateTime newStart, DateTime newEnd, Guid newUserId, IEnumerable<Booking> existingForResource, TimeSpan buffer)
        => existingForResource.Any(b =>
            !b.IsCancelled &&
            Overlaps(newStart, newEnd, b.StartDateTime, b.EndDateTime, b.UserId == newUserId ? TimeSpan.Zero : buffer));

    /// <summary>
    /// Validates the window itself (independent of other bookings).
    /// Returns null when valid, otherwise a human-readable error message.
    /// </summary>
    public static string? ValidateWindow(DateTime start, DateTime end)
    {
        if (end <= start)
            return "EndDateTime must be after StartDateTime.";
        return null;
    }

    //Force a DateTime to be treated as UTC (frontends send ISO-8601 UTC strings)
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
