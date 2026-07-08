# Booking Management Service

A small internal service for booking shared resources (meeting rooms, equipment, etc..): prevents
double-booking, lists and cancels bookings, and ships with a minimal React UI that exercises the
API end to end.

- **Backend:** .NET 10 Minimal API, EF Core + SQLite · **Frontend:** React (Vite) · **Tests:** xUnit
- **Extension task:** *Option 1 — Concurrency* (optimistic concurrency token)

---

## Decisions & Assumptions

- **Overlap = half-open intervals `[start, end)` + a 1-minute turnover buffer.** A booking ending at
  `10:00` frees the resource at `10:01`, not before; the buffer is an explicit `MinimumGap` constant
  (set it to zero for conventional back-to-back bookings). See write-up A.
- **Soft delete.** Cancelling sets `IsCancelled = true`; cancelled bookings are excluded from
  retrieval and overlap checks, but the row is kept for history.
- **UTC everywhere.** SQLite has no timezone-aware type, so a value converter forces read/write as
  UTC and the API always emits a trailing `Z`. The frontend converts local inputs to UTC before sending.
- **Concurrent same-slot creates guarded by a per-resource in-process lock.** Serializes the
  check-then-insert so two simultaneous bookings for one resource can't both win; keyed by `ResourceId`.
- **Concurrency token stamped in `SaveChanges`.** SQLite has no native `rowversion`, so the token is
  set in an override — provider-independent and reliably triggers `DbUpdateConcurrencyException` on
  same-row lost updates (e.g. two cancels).
- **`EnsureCreated` + seed, no migrations.** Keeps clean-checkout startup to a single `dotnet run`
  (seeds 3 users + 3 resources). A real deployment would use EF migrations.
- **Ids are `Guid`; CORS is open** (`AllowAnyOrigin`) for local development.

---

## Design Write-up

**A. Overlap.** Half-open intervals `[start, end)` plus an explicit turnover buffer (`MinimumGap`, 1 min):
two windows conflict unless separated by the buffer — `NewStart < ExistingEnd + buffer && NewEnd + buffer > ExistingStart`.
So a booking ending at `10:00` frees the resource at `10:01`. Making the gap a real parameter (`buffer = 0`
degrades to plain half-open overlap) keeps it a product knob, not a hidden side effect. The rule is a pure,
unit-tested method ([`BookingLogic`](backend/Services/BookingLogic.cs)).

**B. Concurrency.** Single instance, one SQLite file. Two races handled: (1) concurrent same-slot inserts —
check-then-insert wrapped in a **per-resource lock** ([`BookingService`](backend/Services/BookingService.cs)),
so the second create sees the first and gets `409` ([`ConcurrentBookingTests`](backend.Tests/ConcurrentBookingTests.cs));
(2) lost updates on a row — a `RowVersion` token throws `DbUpdateConcurrencyException` → `409`
([`ConcurrencyTests`](backend.Tests/ConcurrencyTests.cs)). The lock is in-process; multi-instance needs the DB fix (D).

**C. At scale.** The **single SQLite writer** serialises all writes, and the per-resource lock is in-process,
so the insert race reopens across instances. Both point to D.

**D. Distributed.** Move to **PostgreSQL** with a `tstzrange` **exclusion constraint**
(`EXCLUDE USING gist (resource_id WITH =, period WITH &&)`) to reject overlaps atomically, then scale the
stateless API horizontally with read replicas for `GET /bookings`.

**E. Tradeoff.** **Simplicity and correctness** over performance — isolated, unit-tested overlap rule; single
monolith, no infra. Where they collided (SQLite dropping timezone info) I chose correctness. Throughput deferred to C/D.

---

## Running it

```bash
cd backend && dotnet run     # API on http://localhost:5080 (seeds SQLite on startup)
cd frontend && npm install && npm run dev   # reads VITE_API_URL, default http://localhost:5080
dotnet test                  # unit tests
```

---

## API

All times are UTC ISO-8601 (e.g. `2026-02-01T09:00:00Z`).

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/resources`, `/users` | List resources / users (for dropdowns). |
| `POST` | `/bookings` | Create a booking. `201`, `400` (bad window), `404` (unknown resource/user), or `409` (overlap or concurrent write). |
| `GET` | `/bookings?resourceId={id}&from={utc}&to={utc}` | List **active** bookings for a resource; optional range (intersects `[from, to)`), sorted by start time. |
| `DELETE` | `/bookings/{id}` | Cancel (soft delete). `204` (idempotent) or `404`. |

`POST /bookings` body: `{ resourceId, userId, startDateTime, endDateTime }`.
