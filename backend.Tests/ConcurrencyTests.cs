using BookingApi.Data;
using BookingApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookingApi.Tests;

/// <summary>
/// Proves the optimistic-concurrency token works against a real (in-memory) SQLite database:
/// two contexts that loaded the same booking cannot both update it.
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ConcurrencyTests()
    {
        // A single shared in-memory connection kept open for the lifetime of the test.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        ctx.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(),
            ResourceId = AppDbContext.Seed.RoomA,
            UserId = AppDbContext.Seed.Alice,
            StartDateTime = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        });
        ctx.SaveChanges();
    }

    [Fact]
    public void ConcurrentCancel_SecondWriterThrows()
    {
        // Arrange — two contexts each load the same booking.
        var id = new AppDbContext(_options).Bookings.Single().Id;

        using var ctx1 = new AppDbContext(_options);
        using var ctx2 = new AppDbContext(_options);

        var b1 = ctx1.Bookings.Single(x => x.Id == id);
        var b2 = ctx2.Bookings.Single(x => x.Id == id);

        // Act — first writer wins.
        b1.IsCancelled = true;
        ctx1.SaveChanges();

        // Assert — second writer loaded the same original RowVersion, so its
        // UPDATE affects 0 rows and throws.
        b2.IsCancelled = true;
        Assert.Throws<DbUpdateConcurrencyException>(() => ctx2.SaveChanges());
    }

    [Fact]
    public void Cancel_ThenRebook_SameWindowIsFree()
    {
        // Arrange
        var id = new AppDbContext(_options).Bookings.Single().Id;

        // Act — cancel the only booking for RoomA.
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Bookings.Single(x => x.Id == id).IsCancelled = true;
            ctx.SaveChanges();
        }

        // Assert — no active bookings remain, so the window is free.
        using (var ctx = new AppDbContext(_options))
        {
            var active = ctx.Bookings.Count(b => b.ResourceId == AppDbContext.Seed.RoomA && !b.IsCancelled);
            Assert.Equal(0, active);
        }
    }

    public void Dispose() => _connection.Dispose();
}
