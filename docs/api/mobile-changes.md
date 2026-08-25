# Changes affecting the mobile client

Newest first. Read the top entry; everything below it you have already lived through.

The full contract is [`attendance-contract-handoff.md`](attendance-contract-handoff.md). Card
matching has its own page: [`mobile-card-matching.md`](mobile-card-matching.md).

---

## 2026-08-25

### Nothing here requires you to release

Both changes are additive. An app built before today keeps working exactly as it does now.

### `localOutcome` is now accepted on a tap

You were already sending it. **The server was discarding it** - the field did not exist on
`TapRequest`, and unknown JSON members are ignored rather than rejected, so every request succeeded
and the value went nowhere.

It is now read and stored, under the name you chose:

```jsonc
{
  "eventId":      "2fa80420-…",
  "cardUid":      "0012503326",
  "deviceTapId":  "a7f3c1e2-…",
  "tappedAt":     "2026-08-25T07:22:11Z",
  "localOutcome": "found"          // optional; omit it and nothing changes
}
```

Same on `POST /attendance/tap` and inside `taps[]` on `POST /attendance/tap/batch`.

Four things worth knowing about how it is treated:

- **It cannot change an outcome.** The server resolves the card itself and rules on the tap exactly
  as it would have without the field. A test pins this. If a device's claim could move a result, then
  attendance would be something a handset asserts rather than something the server determines.
- **Free text.** Send whatever vocabulary you like. It is stored verbatim and never parsed into a
  fixed set, because a value we have not seen must not be able to reject a whole flush - a field that
  exists for reporting must never fail a capture.
- **Truncated at 64 characters**, not refused.
- **Its value is in disagreeing.** A device reporting it *found* a card the server cannot resolve is
  the interesting case: a stale manifest, a sync that never ran, a cloned card, a misreading reader.
  That is the case worth surfacing and it was previously invisible.

### Unresolved scans are now kept

Before today, a scan whose card matched no student was refused and **nothing was recorded** -
`CardNotFound` was an HTTP response and not a row. So "nobody scanned" and "somebody scanned a card
we could not place" looked identical from the back office.

Every such scan is now stored against its event and shown on the event page, with the card serial,
the time, the server's outcome, and your `localOutcome` beside it.

**Nothing changes for your app.** The tap is still refused, with the same `CardNotFound`, and you
should still queue and flush exactly as before. What changed is that somebody can now see it.

Worth knowing when reading that report today: **every student imported from the roster still has no
card**, because the supplied roster carries no RFID column. So most unresolved scans are genuine
cards belonging to real people whose serials have never been loaded - not faults on your side.

---

## Still true, and worth re-checking

These are not new; they are the ones that have caused confusion.

**Match a scanned serial against `cardUids`, never `studentNumber` or `studentId`.** They are
unrelated values from different source columns. Student `2023-0009` carries card `0012503326` -
neither derivable from the other. Full reasoning and a live example in
[`mobile-card-matching.md`](mobile-card-matching.md).

**Offline validation is display-only.** A tap whose UID is missing from your cached manifest **must
still be queued and flushed**. If the server rejects it you get a response and nothing is lost; if
your device discards it, the attendance is gone and afterwards "never scanned" and "scanned then
discarded" are indistinguishable.

**Card serials are strings.** Ten decimal digits with significant leading zeros. `0012503326` through
a numeric type becomes `12503326`, a different card, and it breaks identically on both sides of the
wire so nothing anywhere logs an error.

**A batch is an object, not an array**: `{ "clientClockAt": …, "taps": [ … ] }`. Reconcile against the
per-row results, not the HTTP status - a `200` means the batch was processed, not that every row
succeeded.

**Compare your clock to the manifest's `serverTime` before scanning.** More than five minutes of
drift and every tap returns `TappedAtOutOfRange` and the attendance is lost. On a `304` there is no
body, so take the offset from the HTTP `Date` header.

---

## Planned, and these will require a release

Not scheduled yet. They will land **together**, and you will be told before rather than during -
each one breaks a working app, and shipping them separately would mean three disruptions instead of
one.

- **`GET /events/open`** - your Event Picker currently uses `GET /events`, which is open to anyone.
  When authorization is enforced that endpoint will need a *user* login, which a device key is not
  and will never be. A device-key-gated `/events/open` will replace it for you.
- **`POST /devices` and `POST /devices/{id}/regenerate-key` become back-office only.** Self-enrolment
  stops; a device key will be issued to you instead. Both change together, or locking one leaves the
  other as a way around it.
- **`cachedCardMatch`** may replace `localOutcome` as the field name. If it does, both will be
  accepted for a period - you will not have to change on the day.

Your surface today is five endpoints, and none of the above affects the other four:

```
POST /attendance/tap
POST /attendance/tap/batch
GET  /events/{id}/manifest
GET  /students/by-card/{cardUid}
POST /devices/{id}/heartbeat
```

Everything else in the API is a back-office route. If you find yourself needing one, ask before
building against it - `GET /events/{id}/scans`, for example, now requires a signed-in operator and a
device key is refused.
