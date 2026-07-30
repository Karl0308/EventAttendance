// API-shaped types — a consumed subset of the backend DTOs in `docs/api/openapi.json`.
//
// Deliberately a subset: `StudentDto` also carries firstName/middleName/lastName/gender/photoUrl and
// `EventDto` carries description/requireRegistration, none of which the SPA reads. `api.ts` drops
// them at the boundary rather than widening these types with fields nothing renders.
//
// The `<Dto>PagedResult` envelope the admin lists now return is deliberately NOT here. No component
// consumes it: `api.ts` walks the pages and hands back rows, so paging stays a fact about the wire
// rather than a type spreading through the component tree. Its shape lives in `api.ts` as `PageOf<T>`.
// If a grid ever needs true server-side paging, that is a design change to raise before it is typed.

export interface Card {
  id: string;
  cardUid: string;
  label?: string;
  isActive: boolean;
}

export interface Student {
  id: string;
  studentNumber: string;
  fullName: string;
  email?: string;
  course?: string;
  yearLevel?: string;
  section?: string;
  status: string; // Active/Inactive/Graduated
  cards: Card[];
}

export interface EventItem {
  id: string;
  name: string;
  location?: string;
  startAt: string; // ISO
  endAt: string;
  attendanceMode: string; // Single / TimeInOut
  graceMinutes: number;
  status: string; // Draft/Open/Closed/Cancelled
}

/**
 * The canonical set, verified against the backend's `AttendanceStatus.All` in
 * `EAMS.Domain/DomainValues.cs`. It is documentation, not the wire type: the column is a `string`
 * (see the project's enum-ish-string rule) and `AttendanceDto.status` is declared `string` in the
 * contract, so `AttendanceRecord.status` below stays `string` rather than asserting a union the
 * response cannot prove.
 */
export type AttendanceStatus = "Present" | "Late" | "Absent" | "Excused";

export interface AttendanceRecord {
  id: string;
  eventId: string;
  studentId: string;
  studentName: string;
  studentNumber: string;
  checkInAt?: string;
  checkOutAt?: string;
  status: string; // one of AttendanceStatus above; `string` on the wire
  captureMethod: string; // Rfid/Manual/Import
}

export interface EventSummary {
  eventId: string;
  eventName: string;
  expected: number;
  present: number;
  late: number;
  absent: number;
  excused: number;
  /**
   * Walk-ins: recorded but never invited. Added to match `EventSummaryDto` — the backend moved the
   * over-100% signal out of `attendanceRate` and into this count, so dropping it at the seam would
   * discard the only place that signal now lives. No UI reads it yet.
   */
  unexpected: number;
  /** Share of the *invited* who turned up, as a percentage to one decimal place (0–100). */
  attendanceRate: number;
}
