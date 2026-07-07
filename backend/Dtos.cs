namespace BookingApi;

// Request/response shapes kept separate from EF entities to avoid navigation cycles in JSON.

public record CreateBookingRequest(
    Guid ResourceId,
    Guid UserId,
    DateTime StartDateTime,
    DateTime EndDateTime);

public record BookingResponse(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    Guid UserId,
    string UserName,
    DateTime StartDateTime,
    DateTime EndDateTime,
    bool IsCancelled);

public record ResourceResponse(Guid Id, string Name);

public record UserResponse(Guid Id, string Name);
