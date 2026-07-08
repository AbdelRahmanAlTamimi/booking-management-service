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

**A. How overlap is defined and enforced, and why.** Bookings are **half-open intervals `[start, end)`**
— the conventional model, where back-to-back bookings don't overlap — plus an explicit **turnover buffer**
(`MinimumGap`, 1 minute). Two windows conflict unless separated by at least the buffer:
`NewStart < ExistingEnd + buffer && NewEnd + buffer > ExistingStart`. So a booking ending at `10:00`
frees the resource at `10:01`, not before. Modelling the gap as a real parameter (rather than
faking it with inclusive boundaries) keeps the semantics honest — with `buffer = 0` it degrades to plain
half-open overlap — and makes the buffer a product knob, not a hidden side effect. The rule is a pure,
unit-tested method ([`BookingLogic`](backend/Services/BookingLogic.cs)); `POST /bookings` loads the
resource's **active** bookings (cancelled ones excluded), validates `End > Start`, and coerces times to UTC.

**B. Concurrency assumptions.** The service runs as a **single instance** on one SQLite file. Two
races are handled. (1) *Concurrent inserts of the same slot* — the extension task. The check-then-insert
is wrapped in a **per-resource lock** ([`ResourceLocks`](backend/Services/ResourceLocks.cs),
[`BookingService`](backend/Services/BookingService.cs)), so two simultaneous creates for one resource are
serialized: the second reads the first's committed booking and gets `409` (see
[`ConcurrentBookingTests`](backend.Tests/ConcurrentBookingTests.cs)). Locks are keyed by `ResourceId`, so
different resources never contend. (2) *Lost updates on the same row* — a `RowVersion` optimistic token
makes a stale write affect 0 rows and throw `DbUpdateConcurrencyException` → `409` (see
[`ConcurrencyTests`](backend.Tests/ConcurrencyTests.cs)). The lock is **in-process**, so it only holds for
one instance; the multi-instance fix is a DB-level exclusion constraint (D).

**C. What breaks at scale, first bottleneck.** The **single SQLite writer** — all writes serialise
through one file. And the per-resource lock is **in-process**: run two API instances and each has its own
lock table, so the insert race reopens across instances. Both point to the same fix (D).

**D. Evolving into a distributed system.** Move to **PostgreSQL** and push overlap into the DB as an
**exclusion constraint** over a `tstzrange` (`EXCLUDE USING gist (resource_id WITH =, period WITH &&)`),
eliminating the insert race atomically. Then scale the **stateless API horizontally**, and add read
replicas / caching for the read-heavy `GET /bookings`.

**E. Tradeoff prioritized.** **Simplicity and correctness** over performance: the overlap rule is
isolated and unit-tested, and the app is a single monolith with no infrastructure to stand up. Where
the two collided (SQLite dropping timezone info) I chose correctness. Throughput is deferred to C/D.

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
