import { useEffect, useState } from "react";
import { api } from "./api";

// Deliberately unstyled: this UI exists only to exercise the API end to end.
export default function App() {
  const [resources, setResources] = useState([]);
  const [users, setUsers] = useState([]);
  const [resourceId, setResourceId] = useState("");
  const [userId, setUserId] = useState("");
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [bookings, setBookings] = useState([]);
  const [message, setMessage] = useState(null);

  // Load dropdown data once on mount.
  useEffect(() => {
    api.getResources().then((r) => {
      setResources(r);
      if (r.length) setResourceId((cur) => cur || r[0].id);
    });
    api.getUsers().then((u) => {
      setUsers(u);
      if (u.length) setUserId((cur) => cur || u[0].id);
    });
  }, []);

  // Reload the bookings list whenever the selected resource changes.
  useEffect(() => {
    if (resourceId) refresh();
  }, [resourceId]);

  function refresh() {
    api
      .getBookings(resourceId)
      .then(setBookings)
      .catch((e) => setMessage({ type: "error", text: e.message }));
  }

  async function submit(e) {
    e.preventDefault();
    setMessage(null);
    try {
      // datetime-local values are local time; convert to UTC ISO (trailing Z) for the API.
      const payload = {
        resourceId,
        userId,
        startDateTime: new Date(start).toISOString(),
        endDateTime: new Date(end).toISOString(),
      };
      await api.createBooking(payload);
      setMessage({ type: "ok", text: "Booking created." });
      setStart("");
      setEnd("");
      refresh();
    } catch (err) {
      setMessage({ type: "error", text: err.message });
    }
  }

  async function cancel(id) {
    setMessage(null);
    try {
      await api.cancelBooking(id);
      setMessage({ type: "ok", text: "Booking cancelled." });
      refresh();
    } catch (err) {
      setMessage({ type: "error", text: err.message });
    }
  }

  return (
    <div style={{ maxWidth: 800, margin: "20px auto", fontFamily: "sans-serif" }}>
      <h1>Booking Management</h1>

      {message && (
        <p style={{ color: message.type === "error" ? "red" : "green" }}>
          {message.text}
        </p>
      )}

      <h2>Create a booking</h2>
      <form onSubmit={submit}>
        <div>
          <label>
            Resource:{" "}
            <select value={resourceId} onChange={(e) => setResourceId(e.target.value)}>
              {resources.map((r) => (
                <option key={r.id} value={r.id}>{r.name}</option>
              ))}
            </select>
          </label>
        </div>
        <div>
          <label>
            User:{" "}
            <select value={userId} onChange={(e) => setUserId(e.target.value)}>
              {users.map((u) => (
                <option key={u.id} value={u.id}>{u.name}</option>
              ))}
            </select>
          </label>
        </div>
        <div>
          <label>
            Start:{" "}
            <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} required />
          </label>
        </div>
        <div>
          <label>
            End:{" "}
            <input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} required />
          </label>
        </div>
        <button type="submit">Submit</button>
      </form>

      <h2>Bookings for selected resource</h2>
      <button onClick={refresh}>Refresh</button>
      {bookings.length === 0 ? (
        <p>No active bookings.</p>
      ) : (
        <table border="1" cellPadding="6" style={{ borderCollapse: "collapse", marginTop: 8 }}>
          <thead>
            <tr>
              <th>Resource</th>
              <th>User</th>
              <th>Start (UTC)</th>
              <th>End (UTC)</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {bookings.map((b) => (
              <tr key={b.id}>
                <td>{b.resourceName}</td>
                <td>{b.userName}</td>
                <td>{new Date(b.startDateTime).toISOString()}</td>
                <td>{new Date(b.endDateTime).toISOString()}</td>
                <td>
                  <button onClick={() => cancel(b.id)}>Cancel</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
