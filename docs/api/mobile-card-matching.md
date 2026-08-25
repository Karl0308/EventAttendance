# Card matching on the device — what to compare, and what not to

For the mobile capture app. Companion to
[`attendance-contract-handoff.md`](attendance-contract-handoff.md), which is the full tap contract;
this document covers one question that has come up twice and is worth settling on its own.

---

## The rule

**A scanned serial is compared to `cardUids`. Nothing else.**

Never to `studentId`. Never to `studentNumber`.

## Why, concretely

Here is a real student from the deployed server:

```json
{
  "id":            "d47fb070-dcc0-4cb8-b65a-00de2992d907",
  "studentNumber": "2023-0009",
  "fullName":      "Alyosha Alyosha Alyosha",
  "cards": [
    { "cardUid": "0012503326", "isActive": true }
  ]
}
```

Three different values identify that one person, and the physical card contains **`0012503326`**.

| Field | Value | Can a reader emit it? |
|---|---|---|
| `studentId` | `d47fb070-dcc0-4cb8-b65a-00de2992d907` | No - a database key |
| `studentNumber` | `2023-0009` | No - the institutional REGNO |
| `cardUid` | `0012503326` | **Yes - this is what is on the card** |

`2023-0009` and `0012503326` are not two formats of one value. They are unrelated, they come from
different source columns, and neither can be derived from the other. Matching a scan against
`studentNumber` does not "usually work" or "work with formatting" - it matches nothing, ever.

This is recorded as ADR-001 **D-43**, a client correction dated 2026-07-30. It *reversed* an earlier
assumption that the REGNO was the card number. If you are working from notes written before that
date, they say the opposite of this document, and they are wrong.

The DTO puts it bluntly: a device that falls back to matching the student number when a UID misses
"will attribute a tap to a stranger."

---

## Normalising

Normalise the scanned serial before comparing, and compare the normalised forms:

- uppercase
- strip everything that is not a letter or a digit

The server applies the identical rule (`CardUid.Normalize`) to what you send, and the `cardUids` in
the manifest are already normalised. Normalise first, compare second.

## Strings. Always strings.

Real serials from the CICSS export are **ten decimal digits with significant leading zeros**.

```
"0012503326"   correct
 12503326      a different card that matches nothing
```

Parse one into any numeric type and the leading zeros are gone. It breaks identically on both sides
of the wire, so no error appears anywhere - the tap simply resolves to nobody. Keep it a string in
the scanner buffer, in your local cache, in the queue, and in the JSON body.

## `cardUids` is a list

A reissued card leaves the previous row active (ADR-001 D-3), so a student can legitimately present
either. Match against every entry, not the first.

---

## Offline validation: yes, and display-only

Local validation is what the manifest exists for, so caching it and matching against it offline is
correct. There is one hard rule around it:

> **A tap whose UID is not in your cached manifest must still be queued and flushed.**

Use the match to put a name on the screen. Never use it to decide whether a scan counts. The server
rules on every tap when the queue flushes.

The reason is asymmetric cost. If the server rejects a tap you queued, you get a response saying so
and nothing is lost. If your device discards a tap the server would have accepted, the attendance is
gone - and afterwards "never scanned" and "scanned then discarded" are indistinguishable from any
record anywhere.

### This matters more than it sounds today

**Every student imported from the roster currently has an empty `cardUids`.** The roster the school
supplied carries no RFID column, so `2023-0009` above - created by hand - is at present the only
student in the database with a card at all.

A device that gates on the local match would therefore reject **every scan in the building** except
that one student, and would do it silently. If a whole manifest comes back with no cards on any
attendee, tell the operator the offline cache is unusable rather than offering an index that cannot
hit.

---

## What you send

Single tap - `POST /attendance/tap`. Queue flush - `POST /attendance/tap/batch`. Both require
`Authorization: DeviceKey eams_dk_<keyId>_<secret>`.

One tap:

```jsonc
{
  "eventId":     "2fa80420-7bd7-4b69-8a5a-cc1fd51e3b28",
  "cardUid":     "0012503326",     // the raw scanned serial, normalised, as a string
  "deviceId":    "…",              // optional
  "deviceTapId": "…",              // your idempotency key - see below
  "tappedAt":    "2026-08-25T07:22:31Z"
}
```

A flush is **not** a bare array - it is an object with the queue inside it:

```jsonc
{
  "clientClockAt": "2026-08-25T07:25:00Z",   // the device's clock when the flush was composed
  "taps": [ { /* as above */ }, { /* … */ } ]
}
```

`clientClockAt` is how the server detects a skewed device across a whole flush rather than one row at
a time. The response is a `TapBatchResult` with a per-row `TapBatchRowResult`, so reconcile your
queue against the rows rather than against the HTTP status: a `200` means the batch was processed,
not that every row succeeded. A partial write is safe to retry - the earlier rows come back as
`DuplicateIgnored` because of `deviceTapId`.

An oversized flush is a `400` carrying `maxBatchRows` in the problem body. Chunk from that value
rather than hard-coding a number.

**There is no student field in the tap body, and that is deliberate.** The server resolves the person
from `cardUid`. Whatever you matched locally never reaches us and cannot influence the outcome - so a
local mismatch cannot corrupt attendance, and a local match cannot rescue a bad serial.

`deviceTapId` is the idempotency key the whole offline design rests on: flushing the same queue twice
must not double-count, and the server de-duplicates on it. Generate it once when the tap is captured,
persist it with the queued row, and never regenerate it on retry.

---

## `localOutcome` - not yet accepted

If you are sending a `localOutcome` field today, **the server is discarding it.** No such field
exists on `TapRequest`, and the API does not reject unknown JSON members - it ignores them. The
request succeeds, and the value is gone. Nothing is stored and nothing can be reported from it.

This is a gap to close, not a rejection of the idea. The designed form is `cachedCardMatch`, a
tri-state `Known` / `Unknown` / `NotChecked`, with two properties worth knowing:

- **`NotChecked` is a real, distinct answer.** "I looked and did not find it" and "I had no usable
  cache" are different facts, and collapsing them into one value loses the more useful of the two.
- **The server ignores it for every decision** and always resolves the card itself. Its only job is
  to record what the device believed, so a disagreement can be found afterwards.

It is not built, and building it is a bigger change than adding a field. `CardNotFound` currently
**never reaches the database** - it is a rejection returned to the caller, not a stored row - so
there is nowhere for a not-found scan to be reported from. Making unmatched scans visible under an
event needs somewhere to put them, which is a schema change, not a DTO change.

---

## Checklist

- [ ] Scanned serial is compared to `cardUids`, never `studentNumber` or `studentId`
- [ ] Serial is normalised (uppercase, non-alphanumerics stripped) before comparing
- [ ] Serial is a string end to end - never parsed as a number
- [ ] All entries in `cardUids` are checked, not just the first
- [ ] A cache miss still queues the tap
- [ ] `deviceTapId` is generated once and reused on every retry
- [ ] An entirely cardless manifest warns the operator
- [ ] Device clock is compared to the manifest's `serverTime`; more than five minutes' drift means do
      not scan (every tap would arrive as `TappedAtOutOfRange`)

## Settle this first

Scan a real card and print the raw string the reader produces.

- `0012503326` - a serial, and everything above applies.
- `2023-0009` - a REGNO-encoded card, which is new information nobody has yet. Say so before writing
  any more matching logic; it changes the mapping and needs a deliberate decision, not a workaround.

---

## Related

- [`attendance-contract-handoff.md`](attendance-contract-handoff.md) - the full tap contract, the
  event manifest, and the offline queue
- [`README-for-mobile-developer.md`](README-for-mobile-developer.md) - start here
- [`endpoints.md`](endpoints.md) - the generated endpoint index
