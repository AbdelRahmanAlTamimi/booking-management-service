using BookingApi.Models;
using BookingApi.Services;

namespace BookingApi.Tests;

public class BookingLogicTests
{
    // A fixed reference day so the tests read like a wall clock. All times are UTC.
    private static DateTime At(int hour, int minute = 0) =>
        new(2026, 1, 1, hour, minute, 0, DateTimeKind.Utc);

    // Two distinct users, so "the booker" and "someone else" read clearly in the tests.
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private static Booking Active(DateTime start, DateTime end, Guid? userId = null) =>
        new() { Id = Guid.NewGuid(), ResourceId = Guid.NewGuid(), UserId = userId ?? Bob, StartDateTime = start, EndDateTime = end, IsCancelled = false };

    private static Booking Cancelled(DateTime start, DateTime end)
    {
        var b = Active(start, end);
        b.IsCancelled = true;
        return b;
    }

    // Conflict check as a DIFFERENT user (Alice) against existing bookings owned by Bob, using the
    // app's default turnover buffer (1 minute) — the common case in these tests.
    private static bool Conflict(DateTime start, DateTime end, Booking[] existing) =>
        BookingLogic.HasConflict(start, end, Alice, existing, BookingLogic.MinimumGap);

    // 1 · A valid (non-overlapping) booking succeeds.
    [Fact]
    public void ValidBooking_NoConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(10)) };

        // Act — new booking 11:00–12:00 is well clear of 09:00–10:00.
        var hasConflict = Conflict(At(11), At(12), existing);

        // Assert
        Assert.False(hasConflict);
    }

    // 2 · An overlapping booking is rejected.
    [Fact]
    public void OverlappingBooking_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(11)) };

        // Act — 10:00–12:00 overlaps 09:00–11:00 (from 10:00 to 11:00).
        var hasConflict = Conflict(At(10), At(12), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // 3 · A booking starting exactly when another ends is rejected — the 1-minute buffer isn't met.
    [Fact]
    public void BoundaryTouch_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(10)) };

        // Act — existing ends at 10:00, new starts at 10:00 — no gap.
        var hasConflict = Conflict(At(10), At(11), existing);

        // Assert — blocked because the 1-minute buffer isn't met.
        Assert.True(hasConflict);
    }

    // 3b · The mirror boundary: new ends exactly when existing starts — also blocked.
    [Fact]
    public void BoundaryTouch_Before_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(10), At(11)) };

        // Act — new ends at 10:00 exactly when existing starts.
        var hasConflict = Conflict(At(9), At(10), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // 3c · A gap shorter than the buffer still conflicts: existing ends 10:00, new starts 10:00:30.
    [Fact]
    public void SubBufferGap_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(10)) };
        var thirtySecondsPast = new DateTime(2026, 1, 1, 10, 0, 30, DateTimeKind.Utc);

        // Act — existing ends 10:00, new starts 10:00:30 (gap shorter than buffer).
        var hasConflict = Conflict(thirtySecondsPast, At(11), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // 3d · Exactly one minute past the boundary meets the buffer and is free.
    [Fact]
    public void OneMinuteAfterBoundary_IsNotConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(10)) };

        // Act — new starts exactly one minute past the boundary, meeting the buffer.
        var hasConflict = Conflict(At(10, 1), At(11), existing);

        // Assert
        Assert.False(hasConflict);
    }

    // 3e · A DIFFERENT user cannot book back-to-back: the 1-minute turnover gap is mandatory.
    [Fact]
    public void DifferentUser_BackToBack_IsConflict()
    {
        // Arrange — existing booking belongs to Bob.
        var existing = new[] { Active(At(9), At(10), Bob) };

        // Act — Alice tries to start at 10:00 the moment Bob's booking ends.
        var hasConflict = BookingLogic.HasConflict(At(10), At(11), Alice, existing, BookingLogic.MinimumGap);

        // Assert — blocked; the turnover buffer is never waived for another user.
        Assert.True(hasConflict);
    }

    // 3f · The SAME user MAY book back-to-back against their own booking — no gap required.
    [Fact]
    public void SameUser_BackToBack_IsAllowed()
    {
        // Arrange — existing booking belongs to Alice.
        var existing = new[] { Active(At(9), At(10), Alice) };

        // Act — Alice starts a new booking at 10:00, immediately after her own.
        var hasConflict = BookingLogic.HasConflict(At(10), At(11), Alice, existing, BookingLogic.MinimumGap);

        // Assert — allowed; the gap applies only between different users.
        Assert.False(hasConflict);
    }

    // 3g · The same-user exception waives the gap, NOT genuine overlap: Alice still can't
    // double-book herself for an overlapping window.
    [Fact]
    public void SameUser_ActualOverlap_IsConflict()
    {
        // Arrange — Alice already holds 09:00–11:00.
        var existing = new[] { Active(At(9), At(11), Alice) };

        // Act — Alice tries 10:00–12:00, which truly overlaps her own booking.
        var hasConflict = BookingLogic.HasConflict(At(10), At(12), Alice, existing, BookingLogic.MinimumGap);

        // Assert — real overlap is still rejected.
        Assert.True(hasConflict);
    }

    // 4 · Cancelled bookings do not block a new booking.
    [Fact]
    public void CancelledBooking_DoesNotConflict()
    {
        // Arrange
        var existing = new[] { Cancelled(At(9), At(12)) };

        // Act — same window as a cancelled booking.
        var hasConflict = Conflict(At(9), At(12), existing);

        // Assert — should be free.
        Assert.False(hasConflict);
    }

    // Extra · An identical window to an active booking is rejected.
    [Fact]
    public void IdenticalWindow_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(10)) };

        // Act — an identical window to an active booking.
        var hasConflict = Conflict(At(9), At(10), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // Extra · A new booking fully contained inside an existing one is rejected.
    [Fact]
    public void ContainedWindow_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(9), At(12)) };

        // Act — new booking fully contained inside an existing one.
        var hasConflict = Conflict(At(10), At(11), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // Extra · A new booking that fully contains an existing one is rejected.
    [Fact]
    public void EnclosingWindow_IsConflict()
    {
        // Arrange
        var existing = new[] { Active(At(10), At(11)) };

        // Act — new booking fully contains an existing one.
        var hasConflict = Conflict(At(9), At(12), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // Extra · Overlap is evaluated against ALL active bookings, not just the first.
    [Fact]
    public void ConflictWithSecondBooking_IsDetected()
    {
        // Arrange
        var existing = new[] { Active(At(8), At(9)), Active(At(13), At(14)) };

        // Act — overlap is evaluated against ALL active bookings, not just the first.
        var hasConflict = Conflict(At(13, 30), At(15), existing);

        // Assert
        Assert.True(hasConflict);
    }

    // --- Window validation ---

    [Fact]
    public void EndBeforeStart_IsInvalid()
    {
        // Arrange & Act — end (10:00) is before start (11:00).
        var error = BookingLogic.ValidateWindow(At(11), At(10));

        // Assert — a non-null error means the window is invalid.
        Assert.NotNull(error);
    }

    [Fact]
    public void ZeroLengthWindow_IsInvalid()
    {
        // Arrange & Act — start and end are identical.
        var error = BookingLogic.ValidateWindow(At(10), At(10));

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void NormalWindow_IsValid()
    {
        // Arrange & Act — a normal 10:00–11:00 window.
        var error = BookingLogic.ValidateWindow(At(10), At(11));

        // Assert — null means no validation error.
        Assert.Null(error);
    }

    // --- UTC coercion ---

    [Fact]
    public void AsUtc_UnspecifiedKind_IsTreatedAsUtc()
    {
        // Arrange
        var unspecified = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);

        // Act
        var result = BookingLogic.AsUtc(unspecified);

        // Assert
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(10, result.Hour);
    }
}
