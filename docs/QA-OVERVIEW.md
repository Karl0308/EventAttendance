# EAMS — QA Test Script (Back-office + Capture App)

**Who this is for.** A tester with two things and nothing else:

1. A **back-office URL** in a browser — `https://dev.iloilosupermart.com/eams/`
2. The **capture app (APK)** from the mobile developer, installed on an Android handset with an RFID reader

**You do not install, build, or start anything.** No Visual Studio, no Node, no Docker, no command
line. If a step here asks you to run a command, it is a bug in this document — report it.

| | |
|---|---|
| Back-office (admin web) | `https://dev.iloilosupermart.com/eams/` |
| API the app talks to | `https://dev.iloilosupermart.com/eamsapi/api/v1` |
| Snapshot date | 2026-08-24 |
| Spec of record | `Events-Attendance-Monitoring-System-Technical-Plan.md` |

> The **order of this document is the order to test in.** Part 2 creates the data Parts 3–6 depend
> on. Skipping ahead produces empty screens that look like defects and are not.

---

## 0. Read this first — five things that will otherwise waste your day

1. **There is no login.** No username, no password, no sign-in screen. Anyone with the URL is in.
   Deliberate — staff accounts are a later phase. **Do not raise it as a bug.**
2. **The database may start completely empty** — no students, no events, no terms. That is normal for
   this deployment. §1.2 tells you how to check which state you are in.
3. **You cannot tap a card from the back-office.** The web page has no simulate-tap button in this
   build — it is stripped from the deployed version on purpose. **Every tap in this document comes
   from the handset.**
4. **A card only works after somebody binds it to a student.** The school's roster export has no RFID
   column yet, so imported students have **no cards** and every tap on them returns *card not found* —
   correct behaviour, not a defect. §3.4 shows you how to bind one so taps can succeed.
5. **Card serial ≠ student number.** `REGNO` (e.g. `2023-0001`) identifies the person. The **RFID
   serial** (e.g. `0012503301`) is what the reader scans. Different values, different fields.

> **The single most damaging mistake in this system:** opening a card serial in Excel. `0012503301`
> becomes `12503301` — the leading zeros are dropped and **the card stops working forever**. Never
> retype a serial, never round-trip one through a spreadsheet cell. Copy and paste as text.

---

## 1. Pre-flight

### 1.1 Reach the back-office

| # | Step | Expect |
|---|---|---|
| 1 | Open `https://dev.iloilosupermart.com/eams/` | Dashboard with tiles, and a left-hand nav |
| 2 | Confirm the nav | Dashboard, Students, Events, Devices, Terms |
| 3 | Click each nav item once | Every page loads — content may be empty, but nothing errors |
| 4 | Reload the browser on `/eams/students` directly | The page loads (does **not** 404) |

> Step 4 tests the deep-link fallback. A 404 there is a real deployment bug — report it with the URL.

**If the page is blank, or tiles show an error panel:** the API is unreachable. That is an environment
problem, not a product defect. Report it to the dev team as "back-office cannot reach the API" with a
screenshot, and stop — nothing below will work.

### 1.2 Which state is this deployment in?

Two possibilities. **Check before you conclude anything is missing.**

| Check | Seeded (demo data) | Empty (clean) |
|---|---|---|
| Students page | ~8 sample students | No students |
| Terms page | Term `2025-2026-1` exists | No terms |
| Devices page | A "Development Kiosk" device | No devices |

- **Seeded** — you can go straight to Part 4 (events) using the sample students, and Part 2 is optional.
- **Empty** — this is the normal state for a clean deployment. **Do Part 2 in order.** Empty screens
  are not defects.

> **If Terms shows an error rather than an empty list on a clean deployment,** the school record may be
> missing — everything is hidden without one. Report it as "no school record; Terms page will not
> load" and ask the dev team to create it. You cannot fix this from the UI.

### 1.3 What to get from the dev team before you start

- Confirmation of the back-office URL above.
- Confirmation the handset is on a network that can reach `dev.iloilosupermart.com`.
- The APK, and which build it is.

### 1.4 How to report a bug from here

1. **Where** — the page name, or the app screen.
2. **What you entered** — exactly, including whether a card serial had leading zeros.
3. **What you expected vs. what happened** — screenshot both sides.
4. **The `traceId`**, if an error message shows one. Quote it verbatim; it finds the exact request in
   the server log.
5. **Check the "Not a defect" box** in the relevant section below before filing.

---

## 2. First-run setup (do this in order on an empty deployment)

This part exists because the system has a genuine dependency chain. Each step unlocks the next.

### 2.1 Create a term — nothing imports without one

| # | Step | Expect |
|---|---|---|
| 1 | Go to **Terms** | Empty list |
| 2 | Click **New term** | Dialog: Term code, School year, Semester, Starts on, Ends on |
| 3 | Term code `2025-2026-1`, school year `2025-2026`, semester `1st Semester`, dates blank | Save is enabled |
| 4 | Save | The term appears in the list |
| 5 | Click **Set as current** on it, confirm | It is now badged as current |

**Now test the refusals — each should show a clear message, never a crash or a blank dialog:**

| Try | Expect |
|---|---|
| Save with the term code blank | "Code is required…" |
| Term code of 51+ characters | Refused, naming the 50-character limit |
| Term code with a **leading space** — paste `" 2025-2026-2"` | **Refused** — it must *not* silently trim the space |
| School year blank, or 21+ characters | Refused, naming the 20-character limit |
| Semester blank, or 31+ characters | Refused, naming the 30-character limit |
| Ends on set **before** Starts on | Refused on the Ends on field |
| A second term with the **same code** | Refused as a duplicate — not a crash |

**Then test the current-term rule, which is the important one:**

| # | Step | Expect |
|---|---|---|
| 1 | Create a second term `2025-2026-2` | Created, **not** current |
| 2 | Set it as current | Badge moves to it |
| 3 | Re-check the first term | It is **no longer** current |
| 4 | Open the Terms page in **two browser tabs**, set current in both quickly | **Exactly one** term is current. Never two. Never an error page |

> **Two terms badged current at once is a serious bug — escalate immediately.**

> **Not a defect (§2.1)**
> - A new term not becoming current by itself. Promotion is always deliberate.
> - No delete button on terms. Deleting a term that an import ran against would strand records.

### 2.2 Get students in — two routes

**Route A — roster import (the real one).** See Part 3. Use this if you have an `.xlsx` roster.

**Route B — add one by hand (fastest for a smoke test).** Students → **New student** → REGNO
`2023-9001`, first name `Test`, last name `Student` → Save. See §3.5 for the field rules.

Either way, **you must then bind a card** (§3.4) before any tap can succeed.

---

## 3. Students, roster import, and card binding

### 3.1 Students list

| # | Step | Expect |
|---|---|---|
| 1 | Go to **Students** | Brief loading state, then the list (or an empty state) |
| 2 | Search a surname | Filters down |
| 3 | Search a REGNO like `2023-0003` | Finds that student — search covers REGNO too |
| 4 | Search `zzzzz` | A clear **"nothing found"** message — not a blank grid, not an error |
| 5 | Search `%` | Finds **nothing**. If it returns **every** student, that is a bug — file it |
| 6 | Page through, if there are enough rows | Paging works, no duplicated or skipped rows |

### 3.2 Roster import — step 1, choose

| # | Step | Expect |
|---|---|---|
| 1 | Students → **Import roster** | A three-step wizard opens on **Choose** |
| 2 | Read the page | It states plainly that **nothing has happened yet** |
| 3 | Open the term dropdown | Your term from §2.1 is listed |
| 4 | Try to continue without picking a term | Blocked — the term is **never** guessed from the file |
| 5 | Attach a **`.csv`** | **Refused immediately**, before upload: there is no CSV format for this |
| 6 | Attach an empty (0 KB) file | Refused immediately |
| 7 | Attach anything over **10 MB** | Refused immediately, naming both sizes |
| 8 | Attach a valid `.xlsx`, click Upload | Moves to **Preview** |

**The roster file must contain these columns** (capitalisation and extra spaces do not matter —
`COURSE_CODE`, `Course Code` and `course  code` are all accepted):

`REGNO` · `STUDENT FIRST NAME` · `STUDENT LAST NAME` · `COLLEGE_NAME` · `PROGRAM` · `SECTION_NAME` ·
`COURSE_CODE` · `COURSE_NAME` · `UA_FULLNAME`

Also read if present: `RFID`, `STUDENT MIDDLE NAME`, `FULL_NAME`, `EMAIL_ID`, `USA_EMAIL`, and the
teacher name/suffix/college columns. **A file missing a required column should be refused with the
column named** — test that with a deliberately broken workbook.

### 3.3 Roster import — steps 2 and 3

**Step 2 — Preview.** Nothing has been written yet.

| # | Step | Expect |
|---|---|---|
| 1 | Read the preview | A sample of about **25** rows, using **your file's own column headings** |
| 2 | Read the counters | Total rows found; warnings shown separately from errors |
| 3 | Click **Check** | Re-reads the status without importing |
| 4 | Click **Run** | Moves to **Results** |

**Step 3 — Results.**

| # | Step | Expect |
|---|---|---|
| 1 | Read the counters | Total / Inserted / Updated / Failed add up to the row count |
| 2 | Open the row detail | Failed rows give a reason; skipped rows give a skip reason |
| 3 | Click **Run again** | **Refused** — a batch that already ran cannot re-run. Upload the file again instead |
| 4 | Go back and import the **exact same file** a second time | Rows come back as **Updated** — students are **not** duplicated |
| 5 | Check the Students list | The imported students are there, with course/year/section filled in |

**Warnings that are normal, not failures:** course title alias, course college adopted, instructor
placeholder, section spans programs, section unspecified, student identity conflict, RFID card
revoked, RFID card from legacy mapping, no change.

### 3.4 Bind an RFID card — the step that makes taps work

**Do this for at least two students before testing the handset.**

| # | Step | Expect |
|---|---|---|
| 1 | Students → open a student → the **cards** dialog | Their cards, or an empty list |
| 2 | Add a card with serial `0012509901` | Saved, shown as active |
| 3 | Add a card to a second student, serial `0012509902` | Saved |
| 4 | **Write both serials down** — you need them on the handset | — |
| 5 | Try adding serial `00-12-50-99-01` to a **third** student | **Refused** — separators are stripped, so it is the same card as step 2 |
| 6 | Add a serial longer than 128 characters | Refused, naming the limit |
| 7 | Add a label longer than 100 characters | Refused, naming the limit |
| 8 | **Revoke** a card | Shows as inactive |

> **Step 5 is the normalisation test.** `00-12-50-99-01` and `0012509901` are **the same card**.
> The system strips separators and uppercases before comparing. If step 5 is *allowed*, that is a real
> bug — two students would answer to one physical card.

> After **revoking** a card (step 8), a tap with it must return *card not found* — verify in Part 6.

### 3.5 Add, edit, delete a student

| Field | Rule |
|---|---|
| REGNO | Required, up to **50** characters. Case is preserved — it is not a card serial |
| First / Last name | Required, up to **100** each |
| Middle name / Email / Gender / Photo URL | Optional — **100** / **256** / **20** / **1000** |
| Status | Active, Inactive, or Graduated. Defaults to Active |

**Refusals to confirm:** blank REGNO; 51-character REGNO; blank last name; a **duplicate REGNO**
(must be refused with a readable message, never a crash).

| # | Step | Expect |
|---|---|---|
| 1 | Edit a middle name, save | Updates in the list |
| 2 | Delete a student, confirm | Row disappears |

> **Course, Year level and Section are read-only and always will be.** They belong to the school's
> student system and arrive only through the import. **Not a defect** — do not file it.

> **Not a defect (§3)**
> - Imported students having no card. Cards are bound separately (§3.4) or in bulk later.
> - A deleted student's past attendance being kept.
> - Card serials displaying uppercase and without dashes — that is the stored form.
> - Buttons temporarily disabled with a message about not being sure whether the import ran. That is
>   deliberate — importing twice is worse than a blocked button.

---

## 4. Register the capture device ⭐

**This is the pairing step between the back-office and the handset.** Everything the app does
afterwards is attributed to the device you create here.

### 4.1 Register the device in the back-office

| # | Step | Expect |
|---|---|---|
| 1 | Go to **Devices** | List of devices, or an empty state |
| 2 | Click **New device** | Dialog: Name, Device type, Reader model |
| 3 | Name `QA Handset 1`, type **Mobile**, reader model e.g. `RN/Expo` | Save enabled |
| 4 | Save | **The API key is displayed — this is the only time you will ever see it** |
| 5 | **Copy the key now.** Paste it somewhere you can read on the handset | Key looks like `eams_dk_XXXXXXXXXXXX_` + a long string |
| 6 | Close the dialog, then look for the key again | **It is gone and cannot be recovered.** Correct behaviour |
| 7 | Check the list | `QA Handset 1` is listed, showing a key ID and an active key standing |

> **Only the key ID is ever shown again — never the secret.** The server stores a one-way hash. If you
> lose the key, you do not recover it, you **regenerate** it (§4.3). This is by design; **not a defect.**

**Field rules to test:**

| Try | Expect |
|---|---|
| Save with the name blank | Refused, naming the requirement |
| Name over 100 characters | Refused, naming the limit |
| Reader model over 100 characters | Refused, naming the limit |
| A device type that is not Mobile / Kiosk / Handheld | Refused |

### 4.2 Pair the handset

> The mobile app is the other developer's build, so **its screen names may differ from the wording
> below.** Match on purpose, not on label, and report anything that has no equivalent at all.

| # | Step | Expect |
|---|---|---|
| 1 | Install and open the app on the handset | It asks for a server address, a device key, or both |
| 2 | Enter the server as `https://dev.iloilosupermart.com/eamsapi/api/v1` | Accepted |
| 3 | Enter (paste) the device key from §4.1 step 5 | Accepted |
| 4 | Save / connect | The app reports it is connected or enrolled |
| 5 | Back-office → **Devices** → your device | **Last seen** / **Last used** updates within a minute or two |

**If the app enrols itself instead** — some builds register the device from the app rather than taking
a pasted key. Both are valid:

| # | Step | Expect |
|---|---|---|
| 1 | In the app, enter a device name and enrol | The app reports success and stores its own key |
| 2 | Back-office → **Devices** | **A new device row appears**, with the name the app sent |
| 3 | Compare against your `QA Handset 1` | Two separate devices — that is correct, each holds its own key |

> **Test both routes if the app supports both.** Register one device by paste, one by self-enrolment.

**Pairing failures to test deliberately:**

| Try | Expect |
|---|---|
| A key with one character changed | Rejected. The app must say so clearly — **not** hang, and **not** silently look connected |
| An empty key | Rejected before any request |
| The correct key, handset off the network | A clear network error, distinguishable from a rejected key |

> On an Android build, a network error can also mean the handset is blocking the connection rather
> than the server being down. If **every** request fails from the app but the back-office works from a
> browser on the same network, say exactly that in the report — do not report it as "the API is down".

### 4.3 Rotate and revoke — the security behaviours

Do these **after** Part 6, so you have a working tap to prove the key mattered.

| # | Step | Expect |
|---|---|---|
| 1 | Devices → your device → **Regenerate key** | A **new** key is shown once. Copy it |
| 2 | Without updating the handset, try to tap | **Refused.** The old key is dead the moment it is replaced |
| 3 | Enter the new key in the app, tap again | Works |
| 4 | Devices → **Revoke key** | Key standing shows revoked |
| 5 | Try to tap | **Refused.** The app should tell you to re-enrol, not retry forever |
| 6 | Edit the device name, save | Updates in the list |

> **Step 5 matters:** an app that silently retries a revoked key forever, or that queues taps with no
> warning that they will never be accepted, is a **bug on the app side** — report it to the mobile
> developer with a screenshot of the device's revoked standing.

> **Not a defect (§4)**
> - The key secret being unrecoverable once the dialog closes.
> - The old key dying instantly on regenerate.
> - A device from a different school being rejected as simply "not registered" rather than "wrong
>   school" — it is deliberately not told which.

---

## 5. Events and audience

### 5.1 Create an event

| # | Step | Expect |
|---|---|---|
| 1 | Events → **New event** | Dialog: Name, Description, Location, Start, End, Attendance mode, Grace minutes |
| 2 | Name `QA Convocation`, start **10 minutes from now**, end **3 hours from now**, mode **Single**, grace `15` | Save enabled |
| 3 | Save | The event is created in status **Draft** |

| Field | Rule |
|---|---|
| Name | Required, up to **200** |
| Description / Location | Optional — up to **2000** / **300** |
| End | Must be **strictly after** Start. Identical times are refused |
| Attendance mode | **Single** (one tap) or **Time In/Out** (tap in, tap out) |
| Grace minutes | **0 to 1440** (24 hours) |

**Refusals to confirm:** blank name; End the same as Start; End before Start; grace `-1`; grace `1441`.

> **Grace decides Present vs Late.** A tap at or before *start + grace* is **Present**; after it,
> **Late**. Set grace to `1` and test both sides of it — that is the only way to prove it works.

### 5.2 Audience — who is expected

| # | Step | Expect |
|---|---|---|
| 1 | Open the event, find the audience panel | Empty to begin with |
| 2 | Add a filter — e.g. Program = `BSIT` | A preview of matching students |
| 3 | Apply it | The attendee list fills |
| 4 | Remove a whole group | Those students leave |
| 5 | Remove a single student | Only that one leaves |

Filters available: **College, Program, Year level, Section, Course.**

| Try | Expect |
|---|---|
| More than **20** filter rows in one set | **Refused**, with a clear message. Not silently trimmed |
| A filter that matches more than **5000** students | **Refused loudly.** A quietly shortened list would be the bug |

> **Make sure your test students are in the audience before tapping.** An audience is what the handset
> downloads as its expected list.

### 5.3 Open, close, cancel

| # | Step | Expect |
|---|---|---|
| 1 | **Open** the event | Status becomes **Open** — taps are now accepted |
| 2 | (After Part 6) **Close** it | Status **Closed**. Taps are refused from now on |
| 3 | Create a throwaway event and **Cancel** it | Status **Cancelled**, drops out of the lists. **Recorded attendance is kept** |
| 4 | Try to tap a **Draft** event from the handset | Refused — it is not open |

> A closed event **freezes its totals**. Close it, delete one attendee, re-read the summary: the
> figures must stay consistent, not quietly re-compute against a different population.

### 5.4 Event detail behaviour

| # | Step | Expect |
|---|---|---|
| 1 | Open the event detail | Summary counts match the attendee list |
| 2 | Turn the handset's WiFi off and on while the page is open | The page still reads correctly |

> If the attendee list ever **fails to load**, the page must say so explicitly. It must **never** show
> an empty list, because an operator would read that as "nobody is expected". Seeing a normal empty
> state during a failure is a **real bug — escalate.**

> **Not a defect (§5)**
> - New events starting as Draft rather than Open.
> - A cancelled event keeping its attendance records.
> - An oversized audience being refused instead of trimmed.

---

## 6. The capture app — tapping cards ⭐

**Preconditions — confirm all four, or the results are meaningless:**

- [ ] The event from §5.1 is **Open**
- [ ] Your test students are in the event's **audience** (§5.2)
- [ ] Those students have **bound cards** (§3.4)
- [ ] The handset is **paired** (§4.2)

### 6.1 Load the event on the handset

| # | Step | Expect |
|---|---|---|
| 1 | In the app, choose the event | `QA Convocation` is listed. If events must be entered by ID, get it from the back-office URL |
| 2 | Let it download the expected list | The app reports how many students are expected |
| 3 | Compare that count against the back-office attendee count | **They match** |

### 6.2 The core tap tests

| # | Step | Expect on the handset | Expect in the back-office |
|---|---|---|---|
| 1 | Tap a bound card **before** start + grace | Accepted — **Present** | The student appears as Present |
| 2 | Tap a different bound card **after** start + grace | Accepted — **Late** | Appears as Late |
| 3 | Tap the **same** card again (event mode **Single**) | Reported as already recorded — **not** a second entry | Still **one** record for that student |
| 4 | Tap a card that is **not bound** to anyone | **Card not found** — a clear, final message | Nothing recorded |
| 5 | Tap a **revoked** card (§3.4 step 8) | **Card not found** | Nothing recorded |
| 6 | Close the event in the back-office, then tap | Refused — the event is not open | Nothing recorded |

> **Step 4 is normal, not an error.** Until the school's roster export carries RFID serials, most real
> cards are unbound and *card not found* is the correct, permanent answer. The app should say so
> plainly and **not** retry it forever.

### 6.3 Time In / Out mode

Create a second event with mode **Time In/Out**, open it, put the same students in the audience.

| # | Step | Expect |
|---|---|---|
| 1 | Tap a card | Checked **in** |
| 2 | Tap the **same** card again | Checked **out** — not rejected as a duplicate |
| 3 | Tap it a third time | No further change |
| 4 | Check the back-office | One record showing both an in and an out time |

### 6.4 Offline capture and sync — the highest-value test

| # | Step | Expect |
|---|---|---|
| 1 | Put the handset in **flight mode** | The app stays usable |
| 2 | Tap **5 different** bound cards | All 5 accepted and queued locally. The app shows a pending count |
| 3 | Check the back-office | **Nothing yet** — correct, they have not been sent |
| 4 | Turn the network back on | The app flushes the queue |
| 5 | Check the back-office | **All 5** appear, with their **original tap times** — not the sync time |
| 6 | Force the app to sync **again** | **No duplicates appear.** The count stays 5 |

> **Step 6 is the single most important test in this document.** The system is built so that re-sending
> the same taps can never double-count them. **If a second sync creates duplicate records, stop
> testing and escalate immediately** — that is a serious defect.

| # | Extended offline test | Expect |
|---|---|---|
| 7 | Offline, tap the same card **twice** | Queued sensibly; after sync, **one** record |
| 8 | Queue 5 taps offline, then **force-close and reopen** the app | The queue survives — taps are not lost |
| 9 | Queue taps offline, then **revoke the device key**, then reconnect | Taps are refused, and the app **says so** rather than retrying silently |

### 6.5 Clock behaviour

| # | Step | Expect |
|---|---|---|
| 1 | Set the handset's clock **2 hours ahead**, tap a card | Refused as out of range — **not** silently recorded at the wrong time |
| 2 | Set the clock back to automatic, tap again | Works |

> Present-vs-Late is decided by the tap's timestamp. A handset with a wrong clock is exactly the
> failure this check exists to catch. Leave the clock on **automatic** for all other testing.

---

## 7. End-to-end scenario — run this last, in one sitting

A single pass proving the whole chain. Do it after everything above passes.

| # | Step | Where |
|---|---|---|
| 1 | Create term `2026-2027-1`, set it current | Back-office → Terms |
| 2 | Import a roster of at least 10 students | Back-office → Students → Import |
| 3 | Bind cards to 3 of them, noting the serials | Back-office → Students → cards |
| 4 | Register device `QA E2E Handset`, copy the key | Back-office → Devices |
| 5 | Pair the handset with that key | Handset |
| 6 | Create event `E2E Test`, start in 5 min, grace 10, mode Single | Back-office → Events |
| 7 | Set the audience to the section those 3 students are in | Back-office → event detail |
| 8 | Confirm all 3 are listed as expected | Back-office |
| 9 | Open the event | Back-office |
| 10 | Load the event on the handset, confirm it expects 3 | Handset |
| 11 | Tap student 1 **within** grace | Handset → Present |
| 12 | Go offline, tap students 2 and 3 | Handset → queued |
| 13 | Come back online, sync | Handset → flushed |
| 14 | Confirm all 3 recorded, with the right Present/Late split | Back-office |
| 15 | Sync **again** | **No duplicates** |
| 16 | Close the event | Back-office |
| 17 | Tap student 1 again | **Refused** — event closed |
| 18 | Confirm the final totals still read correctly | Back-office |

**Sign-off criteria:** every step behaves as described, step 15 produces no duplicates, and no step
produces an unexplained error page.

---

## 8. Dashboard

| # | Step | Expect |
|---|---|---|
| 1 | Open the Dashboard | Tiles: students, events, open events, checked in |
| 2 | Add a student, return | The student count goes up |
| 3 | Open an event, return | The open-event count goes up |
| 4 | Record taps, return | The checked-in count reflects them |

> If the API is unreachable, the tiles must show an **error**, not zeros. Tiles reading "0" during an
> outage would be a bug — a real zero and a failed read must never look the same.

---

## 9. Quick reference — is it a bug?

| What you saw | Verdict |
|---|---|
| No login screen at all | **Not a bug** — staff accounts are a later phase |
| Empty students / events / terms on first visit | **Not a bug** — see §1.2, do Part 2 |
| Term dropdown empty on the import page | **Not a bug** — create a term first (§2.1) |
| A `.csv` roster refused | **Not a bug** — `.xlsx` only |
| Course / Year / Section not editable | **Not a bug** — owned by the school's system, permanent |
| Imported students have no cards | **Not a bug** — bind them (§3.4) |
| *Card not found* on an unbound card | **Not a bug** — the correct, final answer |
| Device key never shown again after the dialog | **Not a bug** — by design |
| Old key stops working right after regenerate | **Not a bug** — that is the point |
| New events start as Draft | **Not a bug** |
| Cancelled event keeps its attendance | **Not a bug** |
| No tap button in the back-office | **Not a bug** — taps come from the handset only |
| Times look a few hours off | **Check first** — the server works in UTC, screens show local time |
| **Syncing twice creates duplicate attendance** | **BUG — escalate now** |
| **A card stops working after a roster import** | **BUG — escalate, attach the roster file** |
| **Two terms badged current at the same time** | **BUG — escalate** |
| **A failed attendee list shown as a normal empty list** | **BUG — escalate** |
| **The same serial accepted for two different students** | **BUG — escalate** |
| **A revoked key still recording taps** | **BUG — escalate** |
| **Searching `%` returns every student** | **BUG** |
| **App looks connected but nothing ever reaches the back-office** | **BUG** — note whether the key was pasted or self-enrolled |

---

## 10. Coverage note — what is not in this build

Do not test for these; they are unbuilt, not broken.

| Area | Status |
|---|---|
| Staff login, roles, permissions | Later phase — every page is open |
| Reports and exports | Later phase |
| Recurring / repeating events | Later phase |
| A live auto-refreshing attendance board | The data exists; the screen does not. Refresh manually |
| Bulk card binding from the roster | Planned — bind by hand for now (§3.4) |
