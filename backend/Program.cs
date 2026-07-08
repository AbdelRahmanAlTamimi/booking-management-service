using BookingApi;
using BookingApi.Data;
using BookingApi.Models;
using BookingApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=bookings.db"));

// Per-resource locks are process-wide, so the guard must be a singleton. BookingService is scoped
// (it depends on the scoped DbContext).
builder.Services.AddSingleton<ResourceLocks>();
builder.Services.AddScoped<BookingService>();

const string CorsPolicy = "frontend";
builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Create the SQLite database + seed data on startup so the app runs from a clean checkout.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors(CorsPolicy);

// --- Resources & Users (for frontend dropdowns) ---

app.MapGet("/resources", async (AppDbContext db) =>
    await db.Resources.OrderBy(r => r.Name)
        .Select(r => new ResourceResponse(r.Id, r.Name))
        .ToListAsync());

app.MapGet("/users", async (AppDbContext db) =>
    await db.Users.OrderBy(u => u.Name)
        .Select(u => new UserResponse(u.Id, u.Name))
        .ToListAsync());

// --- Bookings ---

// Create a booking. The overlap + concurrency guards live in BookingService.
app.MapPost("/bookings", async (CreateBookingRequest req, BookingService bookings, AppDbContext db) =>
{
    var result = await bookings.CreateAsync(req);
    return result.Status switch
    {
        CreateBookingStatus.Created =>
            Results.Created($"/bookings/{result.Booking!.Id}", await ToResponse(db, result.Booking.Id)),
        CreateBookingStatus.InvalidWindow =>
            Results.BadRequest(new { error = result.Error }),
        CreateBookingStatus.UnknownResource or CreateBookingStatus.UnknownUser =>
            Results.NotFound(new { error = result.Error }),
        _ => // Overlap or ConcurrencyConflict
            Results.Conflict(new { error = result.Error }),
    };
});

// List active bookings for a resource, with optional date-range filter.
app.MapGet("/bookings", async (Guid resourceId, DateTime? from, DateTime? to, AppDbContext db) =>
{
    var query = db.Bookings
        .Include(b => b.Resource)
        .Include(b => b.User)
        .Where(b => b.ResourceId == resourceId && !b.IsCancelled);

    if (from is not null)
    {
        var fromUtc = BookingLogic.AsUtc(from.Value);
        query = query.Where(b => b.EndDateTime > fromUtc);
    }
    if (to is not null)
    {
        var toUtc = BookingLogic.AsUtc(to.Value);
        query = query.Where(b => b.StartDateTime < toUtc);
    }

    var results = await query
        .OrderBy(b => b.StartDateTime)
        .Select(b => new BookingResponse(
            b.Id, b.ResourceId, b.Resource!.Name, b.UserId, b.User!.Name,
            b.StartDateTime, b.EndDateTime, b.IsCancelled))
        .ToListAsync();

    return Results.Ok(results);
});

// Cancel a booking (soft delete).
app.MapDelete("/bookings/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var booking = await db.Bookings.FindAsync(id);
    if (booking is null)
        return Results.NotFound();

    if (booking.IsCancelled)
        return Results.NoContent(); // already cancelled — idempotent

    booking.IsCancelled = true;
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { error = "Concurrent modification detected. Please retry." });
    }

    return Results.NoContent();
});

app.Run();

static async Task<BookingResponse> ToResponse(AppDbContext db, Guid id)
{
    var b = await db.Bookings
        .Include(x => x.Resource)
        .Include(x => x.User)
        .FirstAsync(x => x.Id == id);

    return new BookingResponse(
        b.Id, b.ResourceId, b.Resource!.Name, b.UserId, b.User!.Name,
        b.StartDateTime, b.EndDateTime, b.IsCancelled);
}

// Exposed so WebApplicationFactory-based integration tests could reference the entry point if needed.
public partial class Program { }
