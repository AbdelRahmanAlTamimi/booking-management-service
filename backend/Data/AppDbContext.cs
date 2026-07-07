using BookingApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BookingApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Booking> Bookings => Set<Booking>();

    // Fixed GUIDs so seed data is deterministic and the frontend has something to show immediately.
    public static class Seed
    {
        public static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public static readonly Guid RoomA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid RoomB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public static readonly Guid Projector = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    }

    // SQLite stores DateTime as text with no timezone, so EF reads it back as Kind=Unspecified.
    // This converter guarantees every DateTime is written and read as UTC, so the API always
    // serializes booking times with a trailing 'Z' and the frontend can't misread them as local.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasKey(u => u.Id);
        b.Entity<Resource>().HasKey(r => r.Id);

        b.Entity<Booking>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.StartDateTime).HasConversion(UtcConverter);
            e.Property(x => x.EndDateTime).HasConversion(UtcConverter);

            e.HasOne(x => x.Resource)
                .WithMany()
                .HasForeignKey(x => x.ResourceId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Optimistic-concurrency token. SQLite has no native rowversion type, so instead of a
            // store-generated column we stamp a fresh value in SaveChanges (see StampRowVersions).
            // EF still emits "UPDATE ... WHERE Id=@id AND RowVersion=@original", so a stale write
            // affects 0 rows and throws DbUpdateConcurrencyException.
            e.Property(x => x.RowVersion).IsConcurrencyToken();

            // Helps the "active bookings for a resource" query.
            e.HasIndex(x => new { x.ResourceId, x.IsCancelled });
        });

        b.Entity<User>().HasData(
            new User { Id = Seed.Alice, Name = "Alice" },
            new User { Id = Seed.Bob, Name = "Bob" },
            new User { Id = Seed.Carol, Name = "Carol" });

        b.Entity<Resource>().HasData(
            new Resource { Id = Seed.RoomA, Name = "Meeting Room A" },
            new Resource { Id = Seed.RoomB, Name = "Meeting Room B" },
            new Resource { Id = Seed.Projector, Name = "Projector" });
    }

    public override int SaveChanges()
    {
        StampRowVersions();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampRowVersions();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Give every inserted/updated booking a fresh RowVersion. EF keeps the ORIGINAL value for
    /// the WHERE clause, so two contexts that loaded the same row can't both win an update.
    /// </summary>
    private void StampRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Booking>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }
}
