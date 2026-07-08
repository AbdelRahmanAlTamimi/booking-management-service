using BookingApi.Models;

namespace BookingApi.Services;

/// <summary>
/// Pure, dependency-free booking rules. Kept separate from the database so the
/// core logic (overlap detection, validation) is directly unit-testable.
/// </summary>
public static class BookingLogic
{
    /// <summary>
    /// Two closed intervals [start, end] overlap iff newStart &lt;= existingEnd AND newEnd &gt;= existingStart.
    /// Because the intervals are closed, touching boundaries DO overlap:
    /// a booking ending at 10:00 blocks another starting at 10:00 — the resource
    /// becomes available for the next reservation at 10:01.
    /// </summary>
    public static bool Overlaps(DateTime newStart, DateTime newEnd, DateTime existingStart, DateTime existingEnd)
        => newStart <= existingEnd && newEnd >= existingStart;

    /// <summary>
    /// Returns true if the proposed window collides with any ACTIVE existing booking
    /// for the same resource. Cancelled bookings are ignored.
    /// </summary>
    public static bool HasConflict(DateTime newStart, DateTime newEnd, IEnumerable<Booking> existingForResource)
        => existingForResource.Any(b =>
            !b.IsCancelled && Overlaps(newStart, newEnd, b.StartDateTime, b.EndDateTime));

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
