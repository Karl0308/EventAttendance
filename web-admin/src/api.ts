// The single seam to the EAMS .NET API (`EAMS.Api`, base path `/api/v1`). Components import `api`
// and nothing reaches around it.
//
// Shapes are pinned to the committed contract at `docs/api/openapi.json` — camelCase JSON, RFC 7807
// `application/problem+json` on every error. Responses are narrowed field by field on the way in:
// this is a system boundary, so a payload that drifts from the contract fails loud here instead of
// arriving three components deep as `undefined`.

import type {
  Student,
  Card,
  EventItem,
  AttendanceRecord,
  EventSummary,
} from "./types";

// The URL `dotnet run --project backend/EAMS.Api --urls "http://localhost:5080"` serves. Overridden
// per-machine by VITE_API_BASE_URL (see `.env.example`) — the capture device's host is DHCP.
const DEFAULT_BASE_URL = "http://localhost:5080/api/v1";

const baseUrl = (import.meta.env.VITE_API_BASE_URL ?? DEFAULT_BASE_URL).replace(/\/+$/, "");

// ---------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------

/** `network` — never reached the server. `http` — server refused. `malformed` — reply off-contract. */
export type ApiErrorKind = "network" | "http" | "malformed";

/** RFC 7807 body as this API emits it. `title`/`detail` are prose and are reworded freely. */
export interface ApiProblem {
  status: number;
  type?: string;
  title?: string;
  detail?: string;
  instance?: string;
  /** The correlation handle. On every error body this API produces. Quote it when reporting. */
  traceId?: string;
  /**
   * The stable machine-readable outcome token — **branch on this and on `status`, never on
   * `title`/`detail`**. Present on the attendance, students and devices surfaces and on auth /
   * rate-limit refusals; absent on the events write surface.
   */
  code?: string;
  /** Server UTC clock. Present on tap and manual-override failures. */
  serverTime?: string;
}

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  /** HTTP status, or 0 when the failure happened before/outside a response. */
  readonly status: number;
  readonly code: string | undefined;
  readonly traceId: string | undefined;
  readonly problem: ApiProblem | undefined;

  constructor(
    kind: ApiErrorKind,
    status: number,
    message: string,
    problem?: ApiProblem,
    cause?: unknown,
  ) {
    super(message, { cause });
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.code = problem?.code;
    this.traceId = problem?.traceId;
    this.problem = problem;
  }
}

/** Turns anything thrown by this module into one truthful line a component can render. */
export function describeApiError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.traceId ? `${error.message} (traceId ${error.traceId})` : error.message;
  }
  if (error instanceof Error) return error.message;
  return "Unexpected error.";
}

// ---------------------------------------------------------------------------------------------
// Narrowing helpers — the whole boundary check lives here, so no cast hides contract drift
// ---------------------------------------------------------------------------------------------

type Row = Record<string, unknown>;

const isRow = (value: unknown): value is Row =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const offContract = (what: string, detail: string) =>
  new ApiError("malformed", 0, `${what} is off-contract: ${detail}.`);

function asRow(value: unknown, what: string): Row {
  if (!isRow(value)) throw offContract(what, `expected an object, got ${describeType(value)}`);
  return value;
}

function asRows(value: unknown, what: string): Row[] {
  if (!Array.isArray(value)) throw offContract(what, `expected an array, got ${describeType(value)}`);
  return value.map((item, i) => asRow(item, `${what}[${i}]`));
}

const describeType = (value: unknown) => (value === null ? "null" : typeof value);

function reqStr(row: Row, key: string, what: string): string {
  const value = row[key];
  if (typeof value !== "string") {
    throw offContract(what, `\`${key}\` should be a string, got ${describeType(value)}`);
  }
  return value;
}

function reqNum(row: Row, key: string, what: string): number {
  const value = row[key];
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw offContract(what, `\`${key}\` should be a finite number, got ${describeType(value)}`);
  }
  return value;
}

function reqBool(row: Row, key: string, what: string): boolean {
  const value = row[key];
  if (typeof value !== "boolean") {
    throw offContract(what, `\`${key}\` should be a boolean, got ${describeType(value)}`);
  }
  return value;
}

/** Nullable-in-contract fields: JSON `null` and an absent key both collapse to `undefined`. */
const optStr = (value: unknown): string | undefined =>
  typeof value === "string" ? value : undefined;

// ---------------------------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------------------------

function buildUrl(path: string, query?: Record<string, string | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== "") params.set(key, value);
  }
  const qs = params.toString();
  return `${baseUrl}${path}${qs ? `?${qs}` : ""}`;
}

async function readProblem(res: Response): Promise<ApiProblem> {
  let body: unknown;
  try {
    body = await res.json();
  } catch {
    // A non-JSON error body (proxy HTML, an empty 502) is itself information, but it is not fatal
    // and it must not mask the status. Nothing is swallowed: the caller still gets an ApiError
    // built from `res.status` below.
    body = undefined;
  }
  const row = isRow(body) ? body : {};
  return {
    status: res.status,
    type: optStr(row.type),
    title: optStr(row.title),
    detail: optStr(row.detail),
    instance: optStr(row.instance),
    traceId: optStr(row.traceId),
    code: optStr(row.code),
    serverTime: optStr(row.serverTime),
  };
}

function httpError(what: string, problem: ApiProblem): ApiError {
  const reason = problem.code ?? problem.title ?? problem.detail ?? "no detail supplied";
  return new ApiError("http", problem.status, `${what} failed (${problem.status}: ${reason}).`, problem);
}

async function send(what: string, path: string, query?: Record<string, string | undefined>) {
  try {
    return await fetch(buildUrl(path, query), {
      headers: { Accept: "application/json" },
    });
  } catch (cause) {
    throw new ApiError(
      "network",
      0,
      `Cannot reach the EAMS API at ${baseUrl} — ${what} was not sent.`,
      undefined,
      cause,
    );
  }
}

async function parseBody(res: Response, what: string): Promise<unknown> {
  try {
    return await res.json();
  } catch (cause) {
    throw new ApiError("malformed", res.status, `${what} returned a body that is not JSON.`, undefined, cause);
  }
}

async function getJson(what: string, path: string, query?: Record<string, string | undefined>) {
  const res = await send(what, path, query);
  if (!res.ok) throw httpError(what, await readProblem(res));
  return parseBody(res, what);
}

/**
 * As `getJson`, but a 404 means "no such row" rather than a failure — the three lookup methods are
 * typed `T | undefined` and the pages already render a loading/empty state for it. Every other
 * non-2xx still throws.
 */
async function getJsonOrMissing(
  what: string,
  path: string,
): Promise<unknown | undefined> {
  const res = await send(what, path);
  if (res.status === 404) return undefined;
  if (!res.ok) throw httpError(what, await readProblem(res));
  return parseBody(res, what);
}

// ---------------------------------------------------------------------------------------------
// DTO mappers
// ---------------------------------------------------------------------------------------------

function toCard(row: Row, what: string): Card {
  return {
    id: reqStr(row, "id", what),
    cardUid: reqStr(row, "cardUid", what),
    label: optStr(row.label),
    isActive: reqBool(row, "isActive", what),
  };
}

function toStudent(row: Row, what: string): Student {
  // StudentDto also carries firstName / middleName / lastName / gender / photoUrl. Nothing in the
  // SPA reads them, so they are dropped here rather than widened into `Student`.
  return {
    id: reqStr(row, "id", what),
    studentNumber: reqStr(row, "studentNumber", what),
    fullName: reqStr(row, "fullName", what),
    email: optStr(row.email),
    course: optStr(row.course),
    yearLevel: optStr(row.yearLevel),
    section: optStr(row.section),
    status: reqStr(row, "status", what),
    cards: asRows(row.cards, `${what}.cards`).map((card, i) => toCard(card, `${what}.cards[${i}]`)),
  };
}

function toEvent(row: Row, what: string): EventItem {
  // EventDto also carries description / requireRegistration — unused by the SPA today.
  return {
    id: reqStr(row, "id", what),
    name: reqStr(row, "name", what),
    location: optStr(row.location),
    startAt: reqStr(row, "startAt", what),
    endAt: reqStr(row, "endAt", what),
    attendanceMode: reqStr(row, "attendanceMode", what),
    graceMinutes: reqNum(row, "graceMinutes", what),
    status: reqStr(row, "status", what),
  };
}

function toAttendance(row: Row, what: string): AttendanceRecord {
  return {
    id: reqStr(row, "id", what),
    eventId: reqStr(row, "eventId", what),
    studentId: reqStr(row, "studentId", what),
    studentName: reqStr(row, "studentName", what),
    studentNumber: reqStr(row, "studentNumber", what),
    checkInAt: optStr(row.checkInAt),
    checkOutAt: optStr(row.checkOutAt),
    status: reqStr(row, "status", what),
    captureMethod: reqStr(row, "captureMethod", what),
  };
}

function toSummary(row: Row, what: string): EventSummary {
  return {
    eventId: reqStr(row, "eventId", what),
    eventName: reqStr(row, "eventName", what),
    expected: reqNum(row, "expected", what),
    present: reqNum(row, "present", what),
    late: reqNum(row, "late", what),
    absent: reqNum(row, "absent", what),
    excused: reqNum(row, "excused", what),
    unexpected: reqNum(row, "unexpected", what),
    attendanceRate: reqNum(row, "attendanceRate", what),
  };
}

// ---------------------------------------------------------------------------------------------
// Methods
// ---------------------------------------------------------------------------------------------

async function listStudents(filter?: {
  search?: string;
  course?: string;
  status?: string;
}): Promise<Student[]> {
  // WARNING — `course` is a LOSSY filter. It maps to `GET /students?course=`, which filters
  // `Students.Course`: the ADR-001 D-2 derived, read-only cache. That column is single-valued, so
  // for a student enrolled in more than one section it can only name one of them. The filter
  // therefore returns a plausible, non-empty, INCOMPLETE answer — 12 of 52 students in the real
  // roster (~23%) sit in 2+ sections. Wired because the signature has always had it; do not build
  // UI that treats the result as the full set for a course.
  const what = "GET /students";
  const body = await getJson(what, "/students", {
    search: filter?.search,
    course: filter?.course,
    status: filter?.status,
  });
  return asRows(body, what).map((row, i) => toStudent(row, `${what}[${i}]`));
}

async function getStudent(id: string): Promise<Student | undefined> {
  const what = "GET /students/{id}";
  const body = await getJsonOrMissing(what, `/students/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toStudent(asRow(body, what), what);
}

async function listEvents(status?: string): Promise<EventItem[]> {
  const what = "GET /events";
  const body = await getJson(what, "/events", { status });
  const events = asRows(body, what).map((row, i) => toEvent(row, `${what}[${i}]`));
  // Newest first — the order the dashboard and the events list have always rendered. The contract
  // makes no ordering promise, so it stays a client concern rather than an assumption.
  return events.sort((a, b) => b.startAt.localeCompare(a.startAt));
}

async function getEvent(id: string): Promise<EventItem | undefined> {
  const what = "GET /events/{id}";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toEvent(asRow(body, what), what);
}

async function listAttendance(eventId: string): Promise<AttendanceRecord[]> {
  const what = "GET /attendance";
  const body = await getJson(what, "/attendance", { eventId });
  return asRows(body, what).map((row, i) => toAttendance(row, `${what}[${i}]`));
}

async function eventSummary(eventId: string): Promise<EventSummary | undefined> {
  const what = "GET /events/{id}/summary";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(eventId)}/summary`);
  return body === undefined ? undefined : toSummary(asRow(body, what), what);
}

/**
 * Explanation of why an admin-browser tap is refused. One string, one place — the message the
 * Snackbar shows and the reason in the report are the same text.
 */
const TAP_UNAVAILABLE =
  "Simulated taps are disabled against the real API: POST /attendance/tap is the capture surface " +
  "and requires a DeviceKey, which the admin SPA deliberately does not hold.";

/**
 * NOT WIRED — deliberately, and this is a finding rather than an oversight.
 *
 * `POST /api/v1/attendance/tap` is secured by the `DeviceKey` scheme
 * (`Authorization: DeviceKey eams_dk_<keyId>_<secret>`), a credential the plan's §11 scopes to
 * `attendance.capture` and nothing else. Putting one in this bundle would hand a page that can only
 * *display* attendance a key that can *write* it — the precise inversion the contract rejects in its
 * own note on `GET /attendance/live/{eventId}` — and a Vite env var is inlined at build time, so the
 * GitHub Pages artefact would publish a write credential as a static asset.
 *
 * Returns a truthful refusal rather than throwing, so `EventDetail`'s existing Snackbar says
 * something honest instead of the button appearing to do nothing. `POST /attendance/manual` is the
 * admin-scoped alternative (open, no device key) but records `captureMethod: Manual` and skips the
 * grace-period logic, so substituting it silently would change behaviour — that needs JJ's call.
 */
async function tap(_eventId: string, _cardUid: string): Promise<{ ok: boolean; message: string }> {
  return { ok: false, message: TAP_UNAVAILABLE };
}

/**
 * No single endpoint answers this, so it is composed from two real ones rather than an invented
 * third. `GET /events/{id}/roster` knows who is *expected*, which would be the better source, but
 * `EventRosterEntryDto` carries no card UID at all — it cannot drive a UID picker.
 */
async function untappedCards(eventId: string): Promise<{ uid: string; name: string }[]> {
  const [students, recorded] = await Promise.all([listStudents(), listAttendance(eventId)]);
  const tapped = new Set(recorded.map((record) => record.studentId));
  return students.flatMap((student) => {
    if (tapped.has(student.id)) return [];
    // Only active cards: an inactive UID is a guaranteed `CardNotFound`, so offering it would put a
    // choice in the picker that cannot succeed.
    const card = student.cards.find((c) => c.isActive);
    return card ? [{ uid: card.cardUid, name: student.fullName }] : [];
  });
}

export const api = {
  listStudents,
  getStudent,
  listEvents,
  getEvent,
  listAttendance,
  eventSummary,
  tap,
  untappedCards,
};
