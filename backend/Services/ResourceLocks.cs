using System.Collections.Concurrent;

namespace BookingApi.Services;

/// <summary>
/// A per-resource async mutex. Serializes the check-then-insert critical section of a booking
/// create so two concurrent requests for the SAME resource cannot both pass the overlap check
/// before either commits. Locks are keyed by ResourceId, so bookings for different resources
/// never block each other.
///
/// This is a SINGLE-INSTANCE guard (in-process). It fully closes the concurrent-insert race for
/// one API instance; the multi-instance answer is a database-level exclusion constraint (see the
/// "Evolving into a distributed system" section of the README).
/// </summary>
public sealed class ResourceLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Acquires the lock for <paramref name="resourceId"/>. Dispose the returned handle to release
    /// (use with <c>using</c>).
    /// </summary>
    public async Task<IDisposable> AcquireAsync(Guid resourceId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(resourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
