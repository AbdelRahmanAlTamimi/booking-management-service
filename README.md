# Booking Management Service

A small internal service for booking shared resources (meeting rooms, equipment). It prevents
double-booking a resource, lets you list and cancel bookings, and ships with a minimal React UI
that exercises the API end to end.

- **Backend:** .NET 10 Minimal API (single project), EF Core + SQLite
- **Frontend:** React (Vite)
- **Tests:** xUnit
- **Extension task implemented:** *Option 1 — Concurrency* (optimistic concurrency token)

---

## Project layout

```
backend/            .NET 10 Minimal API (BookingApi)
  Models/           User, Resource, Booking entities
  Data/             AppDbContext (mapping, seed data, concurrency token, UTC converter)
  Services/         BookingLogic — pure, unit-tested overlap rules
  Dtos.cs           request/response records
  Program.cs        endpoints + DI + CORS
backend.Tests/      xUnit tests (overlap logic + concurrency)
frontend/           React + Vite app
```

---

## Running it

### Backend (API)

```bash
cd backend
dotnet run
```

On startup the app creates a SQLite file (`bookings.db`) and seeds 3 users and 3 resources, so
there is data to work with immediately. The launch profile serves it on **`http://localhost:5080`**
(matching the frontend default); override with `--urls http://localhost:<port>` if needed.

### Frontend

```bash
cd frontend
npm install
npm run dev
```

The frontend reads the API base URL from `frontend/.env` (`VITE_API_URL`, default
`http://localhost:5080`, which matches the backend launch profile). Open the printed Vite URL; you
can create a booking, list bookings for a resource, and cancel one.

### Tests

```bash
dotnet test
```

---

## API documentation

Base URL depends on how you launch the backend (see above). All times are UTC (ISO-8601, e.g.
`2026-02-01T09:00:00Z`).

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/resources` | List all resources (for dropdowns). |
| `GET` | `/users` | List all users (for dropdowns). |
| `POST` | `/bookings` | Create a booking. Validates the window and overlap. |
| `GET` | `/bookings?resourceId={id}&from={utc}&to={utc}` | List **active** bookings for a resource, optional date-range filter, sorted by start time. |
| `DELETE` | `/bookings/{id}` | Cancel a booking (soft delete). Idempotent. |

### `POST /bookings`

Request:

```json
{
  "resourceId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "userId": "11111111-1111-1111-1111-111111111111",
  "startDateTime": "2026-02-01T09:00:00Z",
  "endDateTime": "2026-02-01T10:00:00Z"
}
```

Responses:

- `201 Created` — booking created (returns the booking with resource/user names).
- `400 Bad Request` — `EndDateTime <= StartDateTime`, or unknown resource/user.
- `409 Conflict` — the window overlaps an existing active booking, **or** a concurrent write was
  detected.

### `GET /bookings`

`resourceId` is required. `from`/`to` are optional UTC bounds; a booking is included when it
**intersects** the `[from, to)` range (`EndDateTime > from` and `StartDateTime < to`). Only active
(non-cancelled) bookings are returned.

### `DELETE /bookings/{id}`

- `204 No Content` — cancelled (or already cancelled — the call is idempotent).
- `404 Not Found` — no booking with that id.

---

## Design write-up

### A. How overlap is defined and enforced, and why

Bookings are modelled as **half-open intervals** `[start, end)`. Two windows overlap iff:

```
NewStart < ExistingEnd  &&  NewEnd > ExistingStart
```

Half-open intervals make the boundary case fall out naturally: a booking ending at `10:00` and
another starting at `10:00` **do not** overlap, so back-to-back bookings are allowed. This is the
intuitive behaviour for rooms/equipment and needs no special-casing.

The rule lives in a pure, dependency-free method
([`BookingLogic.Overlaps` / `HasConflict`](backend/Services/BookingLogic.cs)) so it is directly
unit-testable. On `POST /bookings` the endpoint loads the resource's **active** bookings and runs
the check; cancelled bookings are excluded from the query, so they never block a new booking. The
endpoint also validates `End > Start` and coerces all incoming times to UTC.

### B. Concurrency assumptions

The service is assumed to run as a **single instance** against one SQLite file (SQLite allows one
writer at a time, which serialises writes). The **check-then-insert** on `POST` is not atomic, so
two requests could both pass the overlap check before either commits.

For the **extension task (Option 1)** the `Booking` row carries a `RowVersion` optimistic
concurrency token (see below), and both write endpoints catch `DbUpdateConcurrencyException` and
return `409`. This protects against the *lost-update* race — two clients modifying the **same**
booking (e.g. two cancels, or cancel-vs-update) — where the second writer's `UPDATE ... WHERE
Id=@id AND RowVersion=@original` affects 0 rows and throws. See
[`ConcurrencyTests`](backend.Tests/ConcurrencyTests.cs).

Honest limitation: a concurrency token on the `Booking` row does **not** stop two *concurrent
inserts* of overlapping bookings (they're different new rows). Today that race is narrowed by
SQLite's single-writer model plus the short read→write window; the correct fix at scale is a
database-level guarantee — see C/D.

### C. What breaks at scale, and the first bottleneck

- **Single SQLite writer.** All writes serialise through one file — the first hard bottleneck under
  concurrent booking load.
- **Read-then-check pattern.** `POST` reads active bookings into memory and checks in app code.
  With many bookings per resource and high write concurrency, this both costs a query per create
  and reopens the insert race across multiple processes (the per-row token doesn't cover it).

### D. Evolving into a distributed system

- Move to **PostgreSQL** and push the overlap rule into the database as the source of truth using an
  **exclusion constraint** over a `tstzrange` (`EXCLUDE USING gist (resource_id WITH =, period WITH
  &&)`). The DB then rejects overlapping inserts atomically, eliminating the check-then-insert race
  entirely — no matter how many API instances run.
- Scale the **stateless API horizontally** behind a load balancer; correctness no longer depends on
  a single instance.
- Add read replicas / caching for the read-heavy `GET /bookings` path, and partition by resource if
  a single resource becomes hot.

### E. Which tradeoff was prioritized

**Simplicity and correctness**, over performance. The overlap rule is isolated and thoroughly
unit-tested; the design is a single readable monolith with no infrastructure to stand up (SQLite is
file-based). Where simplicity and correctness collided — e.g. SQLite returning `DateTime` without a
timezone — I chose correctness (a UTC value converter so times always round-trip as UTC). Raw
throughput is explicitly deferred to the scaling path in C/D.

---

## Notable decisions & assumptions

- **Soft delete.** Cancelling sets `IsCancelled = true`. Cancelled bookings are excluded from
  retrieval and from overlap checks, but the row is kept (useful for history/audit).
- **UTC everywhere.** All times are stored and returned as UTC. Because SQLite has no timezone-aware
  type and EF reads `DateTime` back as `Unspecified`, a value converter
  ([`AppDbContext`](backend/Data/AppDbContext.cs)) forces read/write as UTC so the API always emits
  a trailing `Z`. The frontend converts its local `datetime-local` inputs to UTC ISO before sending.
- **Concurrency token on SQLite.** SQLite has no native `rowversion`. Rather than a store-generated
  column (which needs a trigger + read-back on SQLite), the token is stamped in a `SaveChanges`
  override — simple, provider-independent, and it reliably triggers `DbUpdateConcurrencyException`.
- **`EnsureCreated` + seed, no migrations.** For a self-contained take-home this keeps
  clean-checkout startup to a single `dotnet run`. A real deployment would use EF migrations.
- **Ids are `Guid`.** For resources, users, and bookings.
- **CORS is open** (`AllowAnyOrigin`) for easy local development.
