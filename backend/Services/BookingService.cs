using BookingApi.Data;
using BookingApi.Models;
using Microsoft.EntityFrameworkCore;

namespace BookingApi.Services;

public enum CreateBookingStatus
{
    Created,
    InvalidWindow,
    UnknownResource,
    UnknownUser,
    Overlap,
    ConcurrencyConflict,
}

public record CreateBookingResult(CreateBookingStatus Status, Booking? Booking = null, string? Error = null);

/// <summary>
/// Owns booking creation, including the overlap guard. The check-then-insert is wrapped in a
/// per-resource lock (<see cref="ResourceLocks"/>) so two simultaneous requests for the same slot
/// are serialized: the second one reads the first's committed booking and is rejected as an overlap.
/// </summary>
public sealed class BookingService(AppDbContext db, ResourceLocks locks)
{
    public async Task<CreateBookingResult> CreateAsync(CreateBookingRequest req, CancellationToken ct = default)
    {
        var start = BookingLogic.AsUtc(req.StartDateTime);
        var end = BookingLogic.AsUtc(req.EndDateTime);

        var windowError = BookingLogic.ValidateWindow(start, end);
        if (windowError is not null)
            return new(CreateBookingStatus.InvalidWindow, Error: windowError);

        if (!await db.Resources.AnyAsync(r => r.Id == req.ResourceId, ct))
            return new(CreateBookingStatus.UnknownResource, Error: "Unknown ResourceId.");
        if (!await db.Users.AnyAsync(u => u.Id == req.UserId, ct))
            return new(CreateBookingStatus.UnknownUser, Error: "Unknown UserId.");

        // Only one create per resource may be in this section at a time. Without it, two requests
        // could both read "no conflict" before either inserts, and both would succeed.
        using (await locks.AcquireAsync(req.ResourceId, ct))
        {
            var active = await db.Bookings
                .Where(x => x.ResourceId == req.ResourceId && !x.IsCancelled)
                .ToListAsync(ct);

            if (BookingLogic.HasConflict(start, end, req.UserId, active, BookingLogic.MinimumGap))
                return new(CreateBookingStatus.Overlap, Error: "The resource is already booked for an overlapping time window.");

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                ResourceId = req.ResourceId,
                UserId = req.UserId,
                StartDateTime = start,
                EndDateTime = end,
                IsCancelled = false,
            };
            db.Bookings.Add(booking);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(CreateBookingStatus.ConcurrencyConflict, Error: "Concurrent modification detected. Please retry.");
            }

            return new(CreateBookingStatus.Created, booking);
        }
    }
}
