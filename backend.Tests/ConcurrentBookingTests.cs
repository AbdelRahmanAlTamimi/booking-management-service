using BookingApi;
using BookingApi.Data;
using BookingApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookingApi.Tests;

/// <summary>
/// Proves the extension task: two users trying to book the SAME slot at the SAME moment cannot
/// both succeed. Each "user" gets its own DbContext (as each HTTP request would), but they share
/// one ResourceLocks registry — exactly how the app wires them (singleton lock, scoped service).
/// </summary>
public class ConcurrentBookingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    private static readonly DateTime Start = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    public ConcurrentBookingTests()
    {
        // One shared in-memory SQLite DB kept alive for the test; RoomB starts with no bookings.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    [Fact]
    public async Task ConcurrentCreate_SameSlot_OnlyOneSucceeds()
    {
        // Arrange — two independent contexts + services (two "requests"), one shared lock registry.
        var locks = new ResourceLocks();
        var req = new CreateBookingRequest(AppDbContext.Seed.RoomB, AppDbContext.Seed.Alice, Start, End);

        using var ctx1 = new AppDbContext(_options);
        using var ctx2 = new AppDbContext(_options);
        var svc1 = new BookingService(ctx1, locks);
        var svc2 = new BookingService(ctx2, locks);

        // Act — fire both at once.
        var results = await Task.WhenAll(svc1.CreateAsync(req), svc2.CreateAsync(req));

        // Assert — exactly one succeeds, one overlaps...
        Assert.Equal(1, results.Count(r => r.Status == CreateBookingStatus.Created));
        Assert.Equal(1, results.Count(r => r.Status == CreateBookingStatus.Overlap));

        // ...and the DB holds exactly one active booking for the slot.
        using var verify = new AppDbContext(_options);
        Assert.Equal(1, verify.Bookings.Count(b => b.ResourceId == AppDbContext.Seed.RoomB && !b.IsCancelled));
    }

    [Fact]
    public async Task ConcurrentCreate_DifferentResources_BothSucceed()
    {
        // Arrange — two requests for two different resources, one shared lock registry.
        var locks = new ResourceLocks();
        var reqA = new CreateBookingRequest(AppDbContext.Seed.RoomA, AppDbContext.Seed.Alice, Start, End);
        var reqB = new CreateBookingRequest(AppDbContext.Seed.RoomB, AppDbContext.Seed.Bob, Start, End);

        using var ctx1 = new AppDbContext(_options);
        using var ctx2 = new AppDbContext(_options);
        var svc1 = new BookingService(ctx1, locks);
        var svc2 = new BookingService(ctx2, locks);

        // Act — fire both at once.
        var results = await Task.WhenAll(svc1.CreateAsync(reqA), svc2.CreateAsync(reqB));

        // Assert — different resources never contend, so both go through.
        Assert.All(results, r => Assert.Equal(CreateBookingStatus.Created, r.Status));
    }

    public void Dispose() => _connection.Dispose();
}
