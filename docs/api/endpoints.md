# EAMS API — endpoint index

<!-- GENERATED FILE — DO NOT EDIT.
     Source: docs/api/openapi.json. Regenerate: node scripts/generate-endpoint-index.mjs --write -->

**Generated from [`openapi.json`](openapi.json) — do not edit by hand.** 54 operations across 9 controllers, all mounted under `/api/v1`. Routes below are written relative to that mount.

This page is a **map, not a contract.** It exists so you can find an endpoint; payload shapes, field types and outcome tokens live in [`openapi.json`](openapi.json), which is what you generate a client from. Behaviour a schema cannot state — what your queue does with each outcome, the card-UID and clock rules — is in [`attendance-contract-handoff.md`](attendance-contract-handoff.md).

`Auth` names the credential an endpoint demands: **DeviceKey** for a capture device, **Bearer** for a signed-in person (`POST /auth/login`). Everything else is still open — enforcement over the rest of the surface is a later phase (ADR-001 D-6), so an unmarked row is not a public endpoint, it is an unprotected one.

## Contents

- [Academic](#academic) — 9
- [Auth](#auth) — 5
- [Devices](#devices) — 7
- [EventManifest](#eventmanifest) — 1
- [SisImport](#sisimport) — 4
- [StudentGroups](#studentgroups) — 1
- [Attendance](#attendance) — 5
- [Events](#events) — 14
- [Students](#students) — 8

## Academic

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/academic/colleges` | — | *none declared* | Every college, by name. |
| `GET` | `/academic/course-offerings` | — | *none declared* | **the section grain, and the row an audience picker is really looking for.** A course taught to two sections is two entries. |
| `GET` | `/academic/courses` | — | *none declared* | Courses, optionally narrowed by college and search term. |
| `GET` | `/academic/programs` | — | *none declared* | Degree programmes, optionally narrowed to one college. |
| `GET` | `/academic/terms` | — | *none declared* | Every term, current first and then newest first. |
| `POST` | `/academic/terms` | — | `400` `409` | Create a school year + semester (D-53). |
| `PUT` | `/academic/terms/{id}` | — | `400` `404` `409` | Edit a term's authored fields (D-53). |
| `PATCH` | `/academic/terms/{id}/current` | — | `400` `404` | Make this the current term, or retire it (D-53). |
| `GET` | `/academic/terms/current` | — | `404` | The term flagged current, or 404 when none is. |

## Auth

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `POST` | `/auth/change-password` | **Bearer** | `401` `422` `429` | Change the caller's own password and end every session. |
| `POST` | `/auth/login` | — | `400` `401` `429` | Exchange an e-mail address and password for an access token and a session. |
| `POST` | `/auth/logout` | **Bearer** | `401` `403` | End this session. |
| `GET` | `/auth/me` | **Bearer** | `401` `404` | Who this access token speaks for, and what it may do. |
| `POST` | `/auth/refresh` | — | `401` `403` `429` | Rotate the session cookie and mint a new access token. |

## Devices

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/devices` | — | *none declared* | Every registered device in this school. |
| `POST` | `/devices` | — | `400` `409` | §6.6 `POST /devices` — register, and issue the first key. |
| `GET` | `/devices/{id}` | — | `404` | One registered device. |
| `PUT` | `/devices/{id}` | — | `400` `404` | §6.6 `PUT /devices/{id}` — the device's own fields. |
| `POST` | `/devices/{id}/heartbeat` | **DeviceKey** | `401` `403` `404` `429` | §6.6 `POST /devices/{id}/heartbeat` — liveness, and one of the four endpoints a device key authenticates. |
| `POST` | `/devices/{id}/regenerate-key` | — | `404` `409` | §6.6 `POST /devices/{id}/regenerate-key` — hard cut, no overlap window. |
| `POST` | `/devices/{id}/revoke-key` | — | `404` | Burn the credential without issuing a replacement. |

## EventManifest

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/events/{id}/manifest` | **DeviceKey** | `400` `401` `403` `404` `409` `413` `429` | The offline capture cache: who is expected at this event and which card resolves to whom. |

## SisImport

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/sis/import/{batchId}` | — | `404` | One import batch and its counts. |
| `GET` | `/sis/import/{batchId}/rows` | — | *none declared* | A batch's staged rows, optionally narrowed to one outcome — `?result=Failed` is the query an operator runs after every import. |
| `POST` | `/sis/import/{batchId}/run` | — | `404` `409` | Runs a staged batch. |
| `POST` | `/sis/import/upload` | — | `400` `422` | Stages a workbook and returns what is in it. |

## StudentGroups

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/student-groups` | — | *none declared* | The audiences an event can be attached to, every filter optional. |

## Attendance

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/attendance` | — | *none declared* | Recorded attendance rows, every filter optional. |
| `GET` | `/attendance/live/{eventId}` | — | `400` `404` `429` | The D-29 cursor-delta poll that stands in for §5/§6.4's SignalR hub. |
| `POST` | `/attendance/manual` | — | `400` `404` | The organizer override (Technical Plan §6.4). |
| `POST` | `/attendance/tap` | **DeviceKey** | `400` `401` `403` `404` `429` | The core capture path (Technical Plan §6.4). |
| `POST` | `/attendance/tap/batch` | **DeviceKey** | `400` `401` `403` `429` | §8.2's offline queue flush (Phase 4d, D-31). |

## Events

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/events` | — | *none declared* | The event list, optionally filtered by status. |
| `POST` | `/events` | — | `400` `409` | §6.3 `POST /events`. |
| `DELETE` | `/events/{id}` | — | `404` | §6.3 `DELETE /events/{id}` — soft (§4.5 `IsDeleted`). |
| `GET` | `/events/{id}` | — | `404` | One event. |
| `PUT` | `/events/{id}` | — | `400` `404` `409` | Update an event. |
| `GET` | `/events/{id}/attendees` | — | `404` | What is currently attached to this event's audience. |
| `POST` | `/events/{id}/attendees` | — | `400` `404` `409` | §6.3 `POST /events/{id}/attendees` — associate `{studentGroupIds[], studentIds[]}`. |
| `DELETE` | `/events/{id}/attendees/groups/{studentGroupId}` | — | `404` `409` | Detaches one group. |
| `DELETE` | `/events/{id}/attendees/students/{studentId}` | — | `404` `409` | Detach one individually-attached student from the event's audience. |
| `GET` | `/events/{id}/roster` | — | `404` | §6.3 `GET /events/{id}/roster` — expected versus actual, and the source of §12's Absentee Report. |
| `GET` | `/events/{id}/scans` | — | `404` | Scans at this event that resolved to no student. |
| `PATCH` | `/events/{id}/status` | — | `400` `404` | §6.3 `PATCH /events/{id}/status` — Open / Close / Cancel. |
| `GET` | `/events/{id}/summary` | — | `404` | The §6.7/§12 Event Attendance Summary. |
| `POST` | `/events/audience/resolve` | — | `400` | How many students a filter matches, who they are, and a preview (D-50/D-51). |

## Students

| Method | Route | Auth | Errors | What it does |
|---|---|---|---|---|
| `GET` | `/students` | — | *none declared* | The roster, every filter optional. |
| `POST` | `/students` | — | `400` `409` | §6.2 `POST /students` — manual roster entry, alongside the §10 bulk import. |
| `DELETE` | `/students/{id}` | — | `404` | §6.2 `DELETE /students/{id}` — soft (§4.3 `IsDeleted`). |
| `GET` | `/students/{id}` | — | `404` | One student by primary key. |
| `PUT` | `/students/{id}` | — | `400` `404` `409` | §6.2 `PUT /students/{id}`. |
| `POST` | `/students/{id}/cards` | — | `400` `404` `409` | §6.2 `POST /students/{id}/cards` — assign an RFID card `{cardUid, label}`. |
| `DELETE` | `/students/{id}/cards/{cardId}` | — | `404` | §6.2 `DELETE /students/{id}/cards/{cardId}` — **deactivate**. |
| `GET` | `/students/by-card/{cardUid}` | **DeviceKey** | `401` `403` `404` `429` | UID→student resolution for the mobile scan screen. |

