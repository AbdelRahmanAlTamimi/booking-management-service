// Base URL of the .NET backend. Override with VITE_API_URL in a .env file if needed.
const BASE = import.meta.env.VITE_API_URL || "http://localhost:5080";

async function handle(res) {
  if (res.status === 204) return null;
  const body = await res.json().catch(() => null);
  if (!res.ok) {
    const message = body?.error || `Request failed (HTTP ${res.status})`;
    throw new Error(message);
  }
  return body;
}

export const api = {
  getResources: () => fetch(`${BASE}/resources`).then(handle),

  getUsers: () => fetch(`${BASE}/users`).then(handle),

  getBookings: (resourceId) =>
    fetch(`${BASE}/bookings?resourceId=${encodeURIComponent(resourceId)}`).then(handle),

  createBooking: (payload) =>
    fetch(`${BASE}/bookings`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    }).then(handle),

  cancelBooking: (id) =>
    fetch(`${BASE}/bookings/${id}`, { method: "DELETE" }).then(handle),
};
