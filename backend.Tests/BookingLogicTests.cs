using BookingApi.Models;
using BookingApi.Services;

namespace BookingApi.Tests;

public class BookingLogicTests
{
    // A fixed reference day so the tests read like a wall clock. All times are UTC.
    private static DateTime At(int hour, int minute = 0) =>
        new(2026, 1, 1, hour, minute, 0, DateTimeKind.Utc);

    private static Booking Active(DateTime start, DateTime end) =>
        new() { Id = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = Guid.NewGuid(), StartDateTime = start, EndDateTime = end, IsCancelled = false };

    private static Booking Cancelled(DateTime start, DateTime end)
    {
        var b = Active(start, end);
        b.IsCancelled = true;
        return b;
    }

    // Conflict check with the app's default turnover buffer (1 minute).
    private static bool Conflict(DateTime start, DateTime end, Booking[] existing) =>
        BookingLogic.HasConflict(start, end, existing, BookingLogic.MinimumGap);

    // 1 · A valid (non-overlapping) booking succeeds.
    [Fact]
    public void ValidBooking_NoConflict()
    {
        var existing = new[] { Active(At(9), At(10)) };
        // New booking 11:00–12:00 is well clear of 09:00–10:00.
        Assert.False(Conflict(At(11), At(12), existing));
    }

    // 2 · An overlapping booking is rejected.
    [Fact]
    public void OverlappingBooking_IsConflict()
    {
        var existing = new[] { Active(At(9), At(11)) };
        // 10:00–12:00 overlaps 09:00–11:00 (from 10:00 to 11:00).
        Assert.True(Conflict(At(10), At(12), existing));
    }

    // 3 · A booking starting exactly when another ends is rejected — the 1-minute buffer isn't met.
    [Fact]
    public void BoundaryTouch_IsConflict()
    {
        var existing = new[] { Active(At(9), At(10)) };
        // Existing ends at 10:00, new starts at 10:00 — no gap, blocked.
        Assert.True(Conflict(At(10), At(11), existing));
    }

    // 3b · The mirror boundary: new ends exactly when existing starts — also blocked.
    [Fact]
    public void BoundaryTouch_Before_IsConflict()
    {
        var existing = new[] { Active(At(10), At(11)) };
        Assert.True(Conflict(At(9), At(10), existing));
    }

    // 3c · A gap shorter than the buffer still conflicts: existing ends 10:00, new starts 10:00:30.
    [Fact]
    public void SubBufferGap_IsConflict()
    {
        var existing = new[] { Active(At(9), At(10)) };
        var thirtySecondsPast = new DateTime(2026, 1, 1, 10, 0, 30, DateTimeKind.Utc);
        Assert.True(Conflict(thirtySecondsPast, At(11), existing));
    }

    // 3d · Exactly one minute past the boundary meets the buffer and is free.
    [Fact]
    public void OneMinuteAfterBoundary_IsNotConflict()
    {
        var existing = new[] { Active(At(9), At(10)) };
        Assert.False(Conflict(At(10, 1), At(11), existing));
    }

    // 3e · With a zero buffer the rule is plain half-open overlap: back-to-back bookings are allowed.
    [Fact]
    public void ZeroBuffer_BackToBack_IsAllowed()
    {
        var existing = new[] { Active(At(9), At(10)) };
        Assert.False(BookingLogic.HasConflict(At(10), At(11), existing, TimeSpan.Zero));
    }

    // 4 · Cancelled bookings do not block a new booking.
    [Fact]
    public void CancelledBooking_DoesNotConflict()
    {
        var existing = new[] { Cancelled(At(9), At(12)) };
        // Same window as a cancelled booking — should be free.
        Assert.False(Conflict(At(9), At(12), existing));
    }

    // Extra · An identical window to an active booking is rejected.
    [Fact]
    public void IdenticalWindow_IsConflict()
    {
        var existing = new[] { Active(At(9), At(10)) };
        Assert.True(Conflict(At(9), At(10), existing));
    }

    // Extra · A new booking fully contained inside an existing one is rejected.
    [Fact]
    public void ContainedWindow_IsConflict()
    {
        var existing = new[] { Active(At(9), At(12)) };
        Assert.True(Conflict(At(10), At(11), existing));
    }

    // Extra · A new booking that fully contains an existing one is rejected.
    [Fact]
    public void EnclosingWindow_IsConflict()
    {
        var existing = new[] { Active(At(10), At(11)) };
        Assert.True(Conflict(At(9), At(12), existing));
    }

    // Extra · Overlap is evaluated against ALL active bookings, not just the first.
    [Fact]
    public void ConflictWithSecondBooking_IsDetected()
    {
        var existing = new[] { Active(At(8), At(9)), Active(At(13), At(14)) };
        Assert.True(Conflict(At(13, 30), At(15), existing));
    }

    // --- Window validation ---

    [Fact]
    public void EndBeforeStart_IsInvalid()
        => Assert.NotNull(BookingLogic.ValidateWindow(At(11), At(10)));

    [Fact]
    public void ZeroLengthWindow_IsInvalid()
        => Assert.NotNull(BookingLogic.ValidateWindow(At(10), At(10)));

    [Fact]
    public void NormalWindow_IsValid()
        => Assert.Null(BookingLogic.ValidateWindow(At(10), At(11)));

    // --- UTC coercion ---

    [Fact]
    public void AsUtc_UnspecifiedKind_IsTreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var result = BookingLogic.AsUtc(unspecified);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(10, result.Hour);
    }
}
