# Booking Management Service

A small internal service for booking shared resources (meeting rooms, equipment, etc..): prevents
double-booking, lists and cancels bookings, and ships with a minimal React UI that exercises the
API end to end.

- **Backend:** .NET 10 Minimal API, EF Core + SQLite · **Frontend:** React (Vite) · **Tests:** xUnit
- **Extension task:** *Option 1 — Concurrency* (optimistic concurrency token)

---

## Decisions & Assumptions

- **Overlap = closed intervals `[start, end]`.** A booking ending at `10:00` blocks another
  starting at `10:00`; the slot is free from `10:01`. See write-up A.
- **Soft delete.** Cancelling sets `IsCancelled = true`; cancelled bookings are excluded from
  retrieval and overlap checks, but the row is kept for history.
- **UTC everywhere.** SQLite has no timezone-aware type, so a value converter forces read/write as
  UTC and the API always emits a trailing `Z`. The frontend converts local inputs to UTC before sending.
- **Concurrency token stamped in `SaveChanges`.** SQLite has no native `rowversion`, so the token is
  set in an override — provider-independent and reliably triggers `DbUpdateConcurrencyException`.
- **`EnsureCreated` + seed, no migrations.** Keeps clean-checkout startup to a single `dotnet run`
  (seeds 3 users + 3 resources). A real deployment would use EF migrations.
- **Ids are `Guid`; CORS is open** (`AllowAnyOrigin`) for local development.

---

## Design Write-up

**A. How overlap is defined and enforced, and why.** Bookings are **closed intervals `[start, end]`**;
two windows overlap iff `NewStart <= ExistingEnd && NewEnd >= ExistingStart`. Touching boundaries
count as a conflict — a booking ending at `10:00` blocks one starting at `10:00`, leaving the resource
free from `10:01`. The rule is a pure, unit-tested method
([`BookingLogic`](backend/Services/BookingLogic.cs)); `POST /bookings` loads the resource's **active**
bookings (cancelled ones excluded), validates `End > Start`, and coerces times to UTC.

**B. Concurrency assumptions.** The service runs as a **single instance** on one SQLite file (one
writer at a time). The check-then-insert on `POST` is not atomic. For the extension task the `Booking`
row carries a `RowVersion` token; both write endpoints catch `DbUpdateConcurrencyException` and return
`409`, protecting against lost updates on the **same** row (see
[`ConcurrencyTests`](backend.Tests/ConcurrencyTests.cs)). Limitation: a per-row token does not stop two
concurrent *inserts* of overlapping bookings — narrowed today by SQLite's single writer, fixed properly at the DB level (D).

**C. What breaks at scale, first bottleneck.** The **single SQLite writer** — all writes serialise
through one file. Secondarily, the **read-then-check** pattern costs a query per create and reopens the
insert race across processes.

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
| `POST` | `/bookings` | Create a booking. `201`, or `400` (bad window / unknown ref), or `409` (overlap or concurrent write). |
| `GET` | `/bookings?resourceId={id}&from={utc}&to={utc}` | List **active** bookings for a resource; optional range (intersects `[from, to)`), sorted by start time. |
| `DELETE` | `/bookings/{id}` | Cancel (soft delete). `204` (idempotent) or `404`. |

`POST /bookings` body: `{ resourceId, userId, startDateTime, endDateTime }`.
