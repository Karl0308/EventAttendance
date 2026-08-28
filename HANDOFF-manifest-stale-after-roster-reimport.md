# Handoff — mobile pull-to-refresh shows stale students after a roster reimport

**Date:** 2026-08-25
**Status:** Diagnosed to a fork; **not** root-caused. No code changed.
**Reported by:** JJ
**Branch at time of analysis:** `feat/backend-phases-0-3a` (clean, `70b71a6`)

---

## Symptom

The Expo mobile app pulls to refresh events and students. The roster is then reimported in
web-admin. The device continues to show the pre-import data.

## Bottom line

**The backend cannot serve a stale manifest.** `version` is a SHA-256 over the exact
response body, recomputed from a live query on every request. There is no cache on the
server at all — verified: no `ResponseCaching`, no `OutputCache`, no `IMemoryCache`, no
`[ResponseCache]` anywhere in `EAMS.Api` or `EAMS.Infrastructure`.

Therefore a `304` after a reimport is the server saying, truthfully, **the manifest content
did not change**. The defect is upstream of the ETag: either the import did not change what
the manifest publishes, or the device is not replacing its copy on a `200`.

This handoff exists so the next person does not re-audit the ETag code. That half is sound.

---

## Code map (verified this session)

| Concern | Location |
|---|---|
| Manifest endpoint, `ETag` / `If-None-Match` / `304` | `backend/EAMS.Api/Controllers/EventManifestController.cs` |
| Version = content hash; `Matches`; `ETagFor` | `backend/EAMS.Application/Dtos/EventManifestVersion.cs` |
| Manifest composition | `backend/EAMS.Infrastructure/Services/EventService.cs:923-1071` |
| Attendee DTO — the five published fields | `backend/EAMS.Infrastructure/Services/EventService.cs:1016-1031` |
| Audience resolution (live group membership) | `backend/EAMS.Infrastructure/Services/EventService.cs:341-374` |
| Audience attachment (explicit group IDs) | `backend/EAMS.Infrastructure/Services/EventService.cs:1848+` |
| Derived-group reconcile and its key | `backend/EAMS.Infrastructure/Services/StudentGroupProjection.cs:401-446` |
| Import pipeline, incl. the projection call | `backend/EAMS.Infrastructure/Sis/SisImportService.cs:196-228` |
| RFID card resolution | `backend/EAMS.Infrastructure/Sis/SisImportService.cs:1286-1355` |
| Student display-cache refresh | `backend/EAMS.Infrastructure/Sis/SisImportService.cs:1472-1512` |
| CORS exposed headers (`Location` only) | `backend/EAMS.Api/Program.cs:67` |
| `/events` not device-gated; `/events/open` planned | `docs/api/mobile-changes.md:101-103` |

### What the manifest actually publishes

Per attendee, five fields and nothing else:

```
studentId · studentNumber · fullName · groupIds · cardUids (active cards only)
```

Plus the event row and each attached group's `(id, name, type)`. `serverTime` and `version`
are structurally excluded from the hash.

**Not published:** `Student.Course`, `Student.Section`, `YearLevel`,
`AcademicCacheUpdatedAt`, term records, enrollments, course offerings.

---

## Ranked hypotheses

### Server side — the import legitimately changed nothing the manifest carries

**H1. The import ran against a different Term.** *(top suspect)*
Derived groups are keyed `(SchoolId, TermId, SourceType=Derived, SourceEntityType,
SourceKey)` and their names carry the term text. A new term produces a **brand-new set of
group rows**. The event is still attached to the previous term's group IDs — attachment is
by explicit ID via `AttachAudienceAsync`, never automatic — and the new import never touches
those rows' membership. The manifest is byte-identical, so `304` forever.

**H2. Updated students landed in groups the event is not attached to.**
A section new to the file becomes a new `StudentGroup` that nobody attached. Its students
are simply not in this event's audience.

**H3. What changed is not a manifest field.** *(very likely if the reimport was to fix
sections / programs / year levels)*
`RefreshStudentCacheAsync` and the projection write exactly the fields listed as "not
published" above. Web-admin shows an updated roster; the device correctly reports no change.
Both are right.

**H4. The RFID rows warned or failed.**
`RfidCardRevoked` creates **no card at all** and leaves the existing one deactivated
(`SisImportService.cs:1316-1336`); `cardUids` is active-only, so nothing moves.
`RfidCardStudentMismatch` fails the row outright. Both are visible on the batch's row
results.

**H5. The batch never ran.** `RunAsync` refuses a batch already `Completed`. A genuinely
idempotent re-run reports every row `Skipped` — correct behavior, not a failure.

### Client side — Expo / React Native

**H6. RN's `fetch` has a native HTTP cache nobody configured.** Android installs an OkHttp
disk cache by default; iOS uses the shared `NSURLCache`. `Cache-Control: private, no-cache`
means "store, but revalidate every time," so it *should* revalidate — but OkHttp may send
its **own** `If-None-Match` and hand JS a `200` with the cached body after a `304` on the
wire. **The network log and what JS receives can legitimately differ. Do not diagnose from
the proxy trace alone.**

**H7. `response.ok` is `false` for `304`.** `ok` is 200–299 only. The common shape
`if (!res.ok) throw` turns every unchanged pull into an error; if that error is swallowed as
"nothing new," a later real change is masked by the same path. Check `304` explicitly, before
any `ok` check.

**H8. The `200` is merged rather than swapped.** A `200` is a **wholesale replacement**. In a
TanStack Query / Zustand / redux-persist setup the usual break is not the fetch — it is
pull-to-refresh invalidating one key while the list renders from another, or a reducer that
merges the new attendee array into the old. A merge keeps every removed student and every
stale card UID indefinitely, and looks exactly like "the refresh did nothing."

**H9. `If-None-Match: *`.** `EventManifestVersion.Matches` returns `304` unconditionally on
`*` (RFC-correct). Any HTTP layer that inserts `*` pins the device permanently.

**H10. Expo web only — `ETag` is unreadable.** `Program.cs:67` exposes only `Location`, so
`res.headers.get('etag')` is `null` in a browser context. Native RN does not enforce CORS, so
this bites only on web previews. Reading `version` from the **body** makes it moot either
way. Note the symptom is the *opposite* (always `200`), so this is not the reported bug.

---

## Discriminator procedure — run in this order

### Step 1 — is the server producing a new version at all?

From a laptop, no `If-None-Match`, so always a `200` with a body:

```bash
curl -sD- -H "Authorization: DeviceKey <key>" \
  https://<host>/api/v1/events/<id>/manifest -o before.json

# reimport the roster in web-admin, then:

curl -sD- -H "Authorization: DeviceKey <key>" \
  https://<host>/api/v1/events/<id>/manifest -o after.json
```

- **`version` identical** → the server is right; it is H1–H5. Stop — no client change can fix
  it. Then check, in order:
  1. the batch's `TermId` versus the `termId` on each attached group (`GET /events/{id}/audience` returns it);
  2. the batch row results for `RfidCardRevoked` / `RfidCardStudentMismatch` / `Skipped`;
  3. whether the changed columns are manifest fields at all (see the list above).
- **`version` differs** → the server is fine; go to Step 2.

### Step 2 — device: which cache is holding it?

**Uninstall and reinstall the app** (not a reload). That clears the OkHttp / `NSURLCache`
directory *and* AsyncStorage together.

- Fresh data after reinstall → the device was holding it → Step 3.
- Still stale after reinstall → the request itself, most likely H9.

### Step 3 — split the two device caches

```js
const res = await fetch(url, {
  headers: { 'If-None-Match': version, Authorization: 'DeviceKey ' + key },
  cache: 'no-store',
});
```

- Fresh with `no-store`, stale without → platform HTTP cache (H6).
- Stale either way → the JS persistence layer is not replacing on `200` (H7 / H8).

---

## What is blocked / still needed

1. **The mobile repo is not in this working copy.** Need the pull-to-refresh handler and
   wherever `version` is stored and read back before H6–H9 can be settled.
2. Which **term** the reimport ran under, versus the term on the event's attached groups.
3. What the reimport was *for* — new students, RFID serials, or corrected sections/programs.
   Only the first two can move the manifest.
4. Whether "event data is stale" also covers the picker (`GET /events`, not device-gated) or
   only the manifest. Those are separate endpoints with separate causes.

## Explicitly out of scope in this session

- No code was changed. No tests were run. No commits.
- The ETag / conditional-GET implementation was audited and is **not** the defect; do not
  re-audit it without new evidence.
- If Step 1 lands on H1/H2, the fix is a term/attachment concern — scope with **ray-backend**,
  gate with **jj-reviewer** before it ships.

## Related

- `docs/api/mobile-changes.md` — the mobile contract, incl. the `/events/open` gap
- `docs/api/README-for-mobile-developer.md:83-84` — manifest refresh cadence and budget
- Memory: `feedback_verify_against_the_deployed_environment.md`,
  `project_eams_login_is_backoffice_only.md`
