# EAMS API — endpoint index

<!-- GENERATED FILE — DO NOT EDIT.
     Source: docs/api/openapi.json. Regenerate: node scripts/generate-endpoint-index.mjs --write -->

**Generated from [`openapi.json`](openapi.json) — do not edit by hand.** 110 operations across 22 controllers, all mounted under `/api/v1`. Routes below are written relative to that mount.

This page is a **map, not a contract.** It exists so you can find an endpoint; payload shapes, field types and outcome tokens live in [`openapi.json`](openapi.json), which is what you generate a client from. Behaviour a schema cannot state — what your queue does with each outcome, the card-UID and clock rules — is in [`attendance-contract-handoff.md`](attendance-contract-handoff.md).

`Auth` names the credential an endpoint demands: **DeviceKey** for a capture device, **Bearer** for a signed-in person (`POST /auth/login`) holding the permission the endpoint requires. Only sign-in and refresh are unmarked.

## Contents

- [Academic](#academic) — 9
- [AttendanceCodes](#attendancecodes) — 3
- [AudienceDefinitions](#audiencedefinitions) — 8
- [Auth](#auth) — 5
- [Cards](#cards) — 1
- [Classifications](#classifications) — 7
- [Clearance](#clearance) — 2
- [Devices](#devices) — 7
- [EventClassifications](#eventclassifications) — 6
- [EventManifest](#eventmanifest) — 1
- [Permissions](#permissions) — 1
- [Personnel](#personnel) — 5
- [Reports](#reports) — 5
- [Roles](#roles) — 6
- [Scans](#scans) — 2
- [SisImport](#sisimport) — 5
- [StudentClassifications](#studentclassifications) — 3
- [StudentGroups](#studentgroups) — 1
- [Users](#users) — 6
- [Attendance](#attendance) — 5
- [Events](#events) — 14
- [Students](#students) — 8

## Academic

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/academic/colleges` | **Bearer** | *none declared* | Every college, by name. |
| `GET` | `/academic/course-offerings` | **Bearer** | *none declared* | **the section grain, and the row an audience picker is really looking for.** A course taught to two sections is two entries. |
| `GET` | `/academic/courses` | **Bearer** | *none declared* | Courses, optionally narrowed by college and search term. |
| `GET` | `/academic/programs` | **Bearer** | *none declared* | Degree programmes, optionally narrowed to one college. |
| `GET` | `/academic/terms` | **Bearer** | *none declared* | Every term, current first and then newest first. |
| `POST` | `/academic/terms` | **Bearer** | `400` `409` | Create a school year + semester (D-53). |
| `PUT` | `/academic/terms/{id}` | **Bearer** | `400` `404` `409` | Edit a term's authored fields (D-53). |
| `PATCH` | `/academic/terms/{id}/current` | **Bearer** | `400` `404` | Make this the current term, or retire it (D-53). |
| `GET` | `/academic/terms/current` | **Bearer** | `404` | The term flagged current, or 404 when none is. |

## AttendanceCodes

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/events/{eventId}/attendance-codes` | **Bearer** | `404` | The codes issued for this event, by attendee name. |
| `POST` | `/events/{eventId}/attendance-codes/email` | **Bearer** | `400` `404` | Email the codes to `All` eligible attendees or a `Selected` subset. |
| `POST` | `/events/{eventId}/attendance-codes/generate` | **Bearer** | `404` | Issue a code to every attendee that lacks one. |

## AudienceDefinitions

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/event-audiences` | **Bearer** | *none declared* | The definitions, active first then by name, optionally filtered to one event classification and/or to active only (the event-creation picker passes both). |
| `POST` | `/event-audiences` | **Bearer** | `400` `409` | Create a definition under an active event classification. |
| `DELETE` | `/event-audiences/{id}` | **Bearer** | `404` | Remove a definition. |
| `GET` | `/event-audiences/{id}` | **Bearer** | `404` | One definition, active or not. |
| `PUT` | `/event-audiences/{id}` | **Bearer** | `400` `404` `409` | A full replacement of the definition's fields. |
| `PATCH` | `/event-audiences/{id}/active` | **Bearer** | `400` `404` | Deactivate a definition, or bring it back. |
| `GET` | `/event-audiences/{id}/attendees` | **Bearer** | `404` | The students and personnel this definition currently resolves to, from the live roster. |
| `GET` | `/event-audiences/options` | **Bearer** | *none declared* | The distinct Academic Community values the criteria pickers are built from, so the form references existing data rather than inventing master data (spec §3/§7). |

## Auth

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `POST` | `/auth/change-password` | **Bearer** | `401` `422` `429` | Change the caller's own password and end every session. |
| `POST` | `/auth/login` | — | `400` `401` `429` | Exchange an e-mail address and password for an access token and a session. |
| `POST` | `/auth/logout` | **Bearer** | `401` `403` | End this session. |
| `GET` | `/auth/me` | **Bearer** | `401` `404` | Who this access token speaks for, and what it may do. |
| `POST` | `/auth/refresh` | — | `401` `403` `429` | Rotate the session cookie and mint a new access token. |

## Cards

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/cards` | **Bearer** | `400` | Find every card whose serial contains a fragment, and who holds it. |

## Classifications

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/classifications` | **Bearer** | *none declared* | The vocabulary, active entries first, then grouped by axis, then by name. |
| `POST` | `/classifications` | **Bearer** | `400` `409` | Add a classification to this school's vocabulary. |
| `DELETE` | `/classifications/{id}` | **Bearer** | `404` `409` | Remove a classification, but only when nobody holds it, nothing points at it, and it is not itself the record of a merge. |
| `GET` | `/classifications/{id}` | **Bearer** | `404` | One entry, retired or not. |
| `PUT` | `/classifications/{id}` | **Bearer** | `400` `404` `409` | Rename a classification. |
| `PATCH` | `/classifications/{id}/active` | **Bearer** | `400` `404` `409` | Retire a classification, or bring it back. |
| `POST` | `/classifications/{id}/merge` | **Bearer** | `400` `404` `409` | Collapse two classifications into one, moving every assignment onto the survivor and deleting nothing. |

## Clearance

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/clearance/students/{studentId}` | **Bearer** | `404` | The clearance report for one student. |
| `GET` | `/clearance/students/{studentId}/export` | **Bearer** | `404` | The same report as a CSV download (CLR-02). |

## Devices

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/devices` | **Bearer** | *none declared* | Every registered device in this school. |
| `POST` | `/devices` | **Bearer** | `400` `409` | §6.6 `POST /devices` — register, and issue the first key. |
| `GET` | `/devices/{id}` | **Bearer** | `404` | One registered device. |
| `PUT` | `/devices/{id}` | **Bearer** | `400` `404` | §6.6 `PUT /devices/{id}` — the device's own fields. |
| `POST` | `/devices/{id}/heartbeat` | **DeviceKey** | `401` `403` `404` `429` | §6.6 `POST /devices/{id}/heartbeat` — liveness, and one of the four endpoints a device key authenticates. |
| `POST` | `/devices/{id}/regenerate-key` | **Bearer** | `404` `409` | §6.6 `POST /devices/{id}/regenerate-key` — hard cut, no overlap window. |
| `POST` | `/devices/{id}/revoke-key` | **Bearer** | `404` | Burn the credential without issuing a replacement. |

## EventClassifications

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/event-classifications` | **Bearer** | *none declared* | The vocabulary, active entries first, then by name. |
| `POST` | `/event-classifications` | **Bearer** | `400` `409` | Add a classification to this school's vocabulary. |
| `DELETE` | `/event-classifications/{id}` | **Bearer** | `404` `409` | Remove a classification, but only when no event references it and it is not one of the seeded three. |
| `GET` | `/event-classifications/{id}` | **Bearer** | `404` | One entry, active or not. |
| `PUT` | `/event-classifications/{id}` | **Bearer** | `400` `404` `409` | Change the display name and/or description. |
| `PATCH` | `/event-classifications/{id}/active` | **Bearer** | `400` `404` | Deactivate a classification, or bring it back. |

## EventManifest

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/events/{id}/manifest` | **DeviceKey** | `400` `401` `403` `404` `409` `413` `429` | The offline capture cache: who is expected at this event and which card resolves to whom. |

## Permissions

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/permissions` | **Bearer** | *none declared* | Every permission code a role may be granted, ordered. |

## Personnel

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/personnel` | **Bearer** | *none declared* | One page of the school's personnel, active first then by name, filtered. |
| `POST` | `/personnel` | **Bearer** | `400` `409` | Create a personnel record. |
| `DELETE` | `/personnel/{id}` | **Bearer** | `404` | Soft-delete the record. |
| `GET` | `/personnel/{id}` | **Bearer** | `404` | One record. |
| `PUT` | `/personnel/{id}` | **Bearer** | `400` `404` `409` | A full replacement of the record's fields. |

## Reports

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/reports/event/{eventId}/attendees` | **Bearer** | `401` `403` `404` | Task 9.6 `GET /reports/event/{eventId}/attendees` — the report's student list, paged: every student who tapped in, with Time In, Time Out and Duration. |
| `GET` | `/reports/event/{eventId}/detail` | **Bearer** | `401` `403` `404` | Task 9.6 `GET /reports/event/{eventId}/detail` — one event's report in detail: its particulars, its summary, and (for a `TimeInOut` event) its time-out totals. |
| `GET` | `/reports/event/{eventId}/export.csv` | **Bearer** | `401` `403` `404` | Task 9.6 `GET /reports/event/{eventId}/export.csv` — the whole single-event report as a CSV download: particulars, totals, and every row of the student list. |
| `GET` | `/reports/event/{eventId}/summary` | **Bearer** | `401` `403` `404` | §6.7 `GET /reports/event/{eventId}/summary` — the Event Attendance Summary for one event. |
| `GET` | `/reports/events/summary` | **Bearer** | `400` `401` `403` `404` | A summary of several hand-picked events: one row per event and their pooled totals. |

## Roles

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/roles` | **Bearer** | *none declared* | Every role, with the count of users in this school that hold it and the codes it grants. |
| `POST` | `/roles` | **Bearer** | `400` `409` | Create a custom role. |
| `DELETE` | `/roles/{id}` | **Bearer** | `404` `409` | Delete a custom role that no user holds. |
| `GET` | `/roles/{id}` | **Bearer** | `404` | One role. |
| `PUT` | `/roles/{id}` | **Bearer** | `400` `404` `409` | Rename and re-describe a custom role. |
| `PUT` | `/roles/{id}/permissions` | **Bearer** | `400` `404` `409` | Replace the set of permission codes a custom role grants. |

## Scans

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/scans/manual-id` | **Bearer** | *none declared* | The recorded manual ID entries for this school, newest first. |
| `POST` | `/scans/manual-id` | **Bearer** | `400` `409` | Record the ID an operator entered for an unrecognized scan. |

## SisImport

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/sis/import/{batchId}` | **Bearer** | `404` | One import batch and its counts. |
| `GET` | `/sis/import/{batchId}/rows` | **Bearer** | *none declared* | A batch's staged rows, optionally narrowed to one outcome — `?result=Failed` is the query an operator runs after every import. |
| `POST` | `/sis/import/{batchId}/run` | **Bearer** | `404` `409` | Runs a staged batch. |
| `GET` | `/sis/import/template` | **Bearer** | `409` | The roster template the school fills in and uploads back. |
| `POST` | `/sis/import/upload` | **Bearer** | `400` `422` | Stages a workbook and returns what is in it. |

## StudentClassifications

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/students/{studentId}/classifications` | **Bearer** | `404` | Everything this person is classified as. |
| `DELETE` | `/students/{studentId}/classifications/{classificationId}` | **Bearer** | `404` `409` | Take this classification off this person. |
| `PUT` | `/students/{studentId}/classifications/{classificationId}` | **Bearer** | `404` `409` | Give this person this classification. |

## StudentGroups

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/student-groups` | **Bearer** | *none declared* | The audiences an event can be attached to, every filter optional. |

## Users

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/users` | **Bearer** | *none declared* | The school's users, active first then by name. |
| `POST` | `/users` | **Bearer** | `400` `409` | Create a user with an initial role and password. |
| `GET` | `/users/{id}` | **Bearer** | `404` | One user. |
| `PUT` | `/users/{id}` | **Bearer** | `400` `404` | Edit the full name and phone. |
| `PATCH` | `/users/{id}/active` | **Bearer** | `400` `404` `409` | Deactivate a user, or bring them back. |
| `PUT` | `/users/{id}/roles` | **Bearer** | `400` `404` `409` | Replace the set of roles the user holds. |

## Attendance

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/attendance` | **Bearer** | `400` | Recorded attendance rows, every filter optional. |
| `GET` | `/attendance/live/{eventId}` | **Bearer** | `400` `404` `429` | The D-29 cursor-delta poll that stands in for §5/§6.4's SignalR hub. |
| `POST` | `/attendance/manual` | **Bearer** | `400` `404` | The organizer override (Technical Plan §6.4). |
| `POST` | `/attendance/tap` | **DeviceKey** | `400` `401` `403` `404` `429` | The core capture path (Technical Plan §6.4). |
| `POST` | `/attendance/tap/batch` | **DeviceKey** | `400` `401` `403` `429` | §8.2's offline queue flush (Phase 4d, D-31). |

## Events

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/events` | **Bearer** **DeviceKey** | `401` `403` | The event list, optionally filtered by status. |
| `POST` | `/events` | **Bearer** | `400` `409` | §6.3 `POST /events`. |
| `DELETE` | `/events/{id}` | **Bearer** | `404` | §6.3 `DELETE /events/{id}` — soft (§4.5 `IsDeleted`). |
| `GET` | `/events/{id}` | **Bearer** | `404` | One event. |
| `PUT` | `/events/{id}` | **Bearer** | `400` `404` `409` | Update an event. |
| `GET` | `/events/{id}/attendees` | **Bearer** | `404` | What is currently attached to this event's audience. |
| `POST` | `/events/{id}/attendees` | **Bearer** | `400` `404` `409` | §6.3 `POST /events/{id}/attendees` — associate `{studentGroupIds[], studentIds[]}`. |
| `DELETE` | `/events/{id}/attendees/groups/{studentGroupId}` | **Bearer** | `404` `409` | Detaches one group. |
| `DELETE` | `/events/{id}/attendees/students/{studentId}` | **Bearer** | `404` `409` | Detach one individually-attached student from the event's audience. |
| `GET` | `/events/{id}/roster` | **Bearer** | `404` | §6.3 `GET /events/{id}/roster` — expected versus actual, and the source of §12's Absentee Report. |
| `GET` | `/events/{id}/scans` | **Bearer** | `401` `403` `404` | Scans at this event that resolved to no student. |
| `PATCH` | `/events/{id}/status` | **Bearer** | `400` `404` | §6.3 `PATCH /events/{id}/status` — Open / Close / Cancel. |
| `GET` | `/events/{id}/summary` | **Bearer** | `404` | The §6.7/§12 Event Attendance Summary. |
| `POST` | `/events/audience/resolve` | **Bearer** | `400` | How many students a filter matches, who they are, and a preview (D-50/D-51). |

## Students

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/students` | **Bearer** | *none declared* | The roster, every filter optional. |
| `POST` | `/students` | **Bearer** | `400` `409` | §6.2 `POST /students` — manual roster entry, alongside the §10 bulk import. |
| `DELETE` | `/students/{id}` | **Bearer** | `404` | §6.2 `DELETE /students/{id}` — soft (§4.3 `IsDeleted`). |
| `GET` | `/students/{id}` | **Bearer** | `404` | One student by primary key. |
| `PUT` | `/students/{id}` | **Bearer** | `400` `404` `409` | §6.2 `PUT /students/{id}`. |
| `POST` | `/students/{id}/cards` | **Bearer** | `400` `404` `409` | §6.2 `POST /students/{id}/cards` — assign an RFID card `{cardUid, label}`. |
| `DELETE` | `/students/{id}/cards/{cardId}` | **Bearer** | `404` | §6.2 `DELETE /students/{id}/cards/{cardId}` — **deactivate**. |
| `GET` | `/students/by-card/{cardUid}` | **DeviceKey** | `401` `403` `404` `429` | UID→student resolution for the mobile scan screen. |

