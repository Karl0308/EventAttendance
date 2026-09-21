# Handoff: sign-in is now enforced — what it means for the capture app

**For:** the mobile (capture app) developer · **Date:** 2026-09-17 · **Effective:** the next API deployment

**Short version: an app that is already set up keeps working with no change.** It keeps using its
device key exactly as it does today. The only thing that changes on your side is **how a brand-new
device gets its key** — see [New devices](#new-devices-no-more-self-enrolment).

---

## Why this changed

Until now, almost every route in the API answered anyone who could reach it: the student roster,
every event, even `POST /devices`, so anyone could mint a working device key. The API is now deployed
on the internet, so every route requires a credential:

- **A person** (the admin web app) signs in and sends `Authorization: Bearer <token>`.
- **A device** (your app) sends `Authorization: DeviceKey eams_dk_<keyId>_<secret>`, as it already does.

---

## What keeps working, unchanged

Same routes, same device key, same requests and responses:

```
GET  /api/v1/events                      ← your event picker
GET  /api/v1/events/{id}/manifest
GET  /api/v1/students/by-card/{cardUid}
POST /api/v1/attendance/tap
POST /api/v1/attendance/tap/batch
POST /api/v1/devices/{id}/heartbeat
```

### Two things to check in your code — no change expected

**1. Your `GET /events` call must send the device key.** It used to be open, so it worked without the
`Authorization` header. It now answers a request without one with `401`. If your HTTP client attaches
the key to every request, as it does for the manifest and taps, there is nothing to do. If the
picker's request is built separately, add the header there.

**2. A device only ever sees `Open` events, for its own school.** That is what the picker needs, and
what `?status=Open` already returned. A device asking for another status still gets open events only.
The response shape is unchanged: `items`, `page`, `pageSize`, `total`, `hasMore`.

| Status | `code` | What your app does |
|---|---|---|
| `200` | — | Show the list. Empty `items` means no event is open right now. |
| `401` | `DeviceKeyMissing` / `DeviceKeyMalformed` / `DeviceKeyInvalid` | The header was missing, or the stored key is wrong. |
| `403` | `DeviceKeyRevoked` / `DeviceInactive` | An administrator retired this key or device. |

### Everything else refuses a device key

Any route not in the list above now answers a device key with `401` and a `WWW-Authenticate: Bearer`
challenge — `GET /events/{id}`, `GET /attendance/live/{eventId}`, `GET /students`, and so on. **If your
app calls one of them, tell us which and why**, and we'll work out a device-safe way to give you that
data.

---

## New devices: no more self-enrolment

`POST /devices` and `POST /devices/{id}/regenerate-key` now need a signed-in administrator. From the app
they return `401`. This does **not** affect a device that already has its key stored — only a fresh
install, a wiped handset, or a lost key.

It had to close: that call handed a working key to anyone who could reach the API, and a key reads
event manifests (student names and card serials) and records attendance.

**The new setup for a device:**

1. An administrator opens **Devices** in the admin web app and registers the device.
2. A dialog shows, once: the **device key** (`eams_dk_…`, secret) and the **device ID** (a GUID, not
   secret).
3. Those two values go into the app, where your self-enrolment step used to store the values from the
   `POST /devices` response: the key in `expo-secure-store` / Android Keystore, and the device ID
   wherever you keep it for heartbeat.

**What this needs from the app:** a way to enter those two values instead of calling `POST /devices`,
by paste or typing. QR scanning is still an open question (#6 in the contract handoff); tell us if you
want it. Until that exists, a new device can only be set up with a build that accepts a key.

**Lost key, or `401 DeviceKeyInvalid` / `403 DeviceKeyRevoked`:** an administrator regenerates the key
on the Devices page; the old one stops working at once. Enter the new key; the device ID does not
change. Wherever [`attendance-contract-handoff.md`](attendance-contract-handoff.md) says **re-enrol**,
read it as "get a new key from an administrator". **Keep the offline queue and the cached manifest**
while you wait.

---

## Checklist

- [ ] The `GET /events` request sends `Authorization: DeviceKey …`.
- [ ] The app calls no route outside the six listed above.
- [ ] A new device can be given a key and device ID without calling `POST /devices`. Needed only for
      new installs and lost keys.

## Testing against it

Ask us for a device key **and** device ID for your own test device; don't share ours, because every
tap is attributed to the device that sent it.

```bash
# Your picker, with the key: expect 200 and open events only
curl -H "Authorization: DeviceKey <key>" https://<host>/api/v1/events

# The same call without the key: expect 401
curl -i https://<host>/api/v1/events

# Self-enrolment is closed: expect 401
curl -i -X POST https://<host>/api/v1/devices -H "Content-Type: application/json" -d '{}'
```

Questions or anything unclear: reply on the usual thread. The changelog entry is in
[`mobile-changes.md`](mobile-changes.md) (2026-09-17).
