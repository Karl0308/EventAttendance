// ADR-015 phase F1: the `src/api.ts` half of personnel attendance — the B1-B3 response shapes the SPA
// already reads, and the 503 the card-UID write lock answers with.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Field names are copied from the backend DTOs (`AttendanceDtoV2`, `EventSummaryDto`,
// `EventAudienceDto`, `EventAudienceResultDto`, `EventReportRowDto`, `EventScanDto`), not from the
// digest, and every new field is checked at the seam so a server that drops one fails loud (`malformed`)
// rather than arriving on a screen as `undefined`:
//
//   - a personnel attendance row has NULL `student*` fields and a `personType` of "Personnel";
//   - `expected` IS the total (students + personnel); `expectedStudents` / `expectedPersonnel` are its
//     parts, and there is no `expectedTotal`;
//   - `personType` and `expectedPersonnelSource` are closed over the values the contract names;
//   - a report row carries the split, the pooled totals do not;
//   - a scan carries `personnelId` and `reason`, both null unless a personnel card was refused.
//
// F1-6: `503` + `code: "WriteLockUnavailable"` + `Retry-After` is read onto `ApiError.retryAfterSeconds`
// on each of the five routes that take the lock.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";
import type { PersonnelWriteRequest } from "../src/types";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";
const STUDENT_ID = "11111111-1111-4111-8111-111111111111";
const PERSONNEL_ID = "22222222-2222-4222-8222-222222222222";
const BATCH_ID = "33333333-3333-4333-8333-333333333333";
const TERM_ID = "55555555-5555-4555-8555-555555555555";

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });

function serve(reply: (method: string, url: string) => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) =>
    Promise.resolve(reply(init?.method ?? "GET", String(input))),
  );
}

async function apiErrorFrom(call: () => Promise<unknown>): Promise<ApiError> {
  try {
    await call();
  } catch (thrown) {
    if (thrown instanceof ApiError) return thrown;
    throw new Error(`Expected an ApiError; got ${String(thrown)}`);
  }
  throw new Error("Expected the call to fail; it succeeded.");
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ---------------------------------------------------------------------------------------------
// Fixtures, in the shape the backend's DTOs serialise to
// ---------------------------------------------------------------------------------------------

const studentRow = (over: Record<string, unknown> = {}) => ({
  id: "a1",
  eventId: EVENT_ID,
  studentId: STUDENT_ID,
  studentName: "Maria Santos",
  studentNumber: "2026-0001",
  checkInAt: "2026-09-01T01:05:00Z",
  checkOutAt: null,
  status: "Present",
  captureMethod: "Rfid",
  personType: "Student",
  personnelId: null,
  personnelNumber: null,
  personnelName: null,
  ...over,
});

const personnelRow = (over: Record<string, unknown> = {}) => ({
  id: "a2",
  eventId: EVENT_ID,
  studentId: null,
  studentName: null,
  studentNumber: null,
  checkInAt: "2026-09-01T01:06:00Z",
  checkOutAt: null,
  status: "Present",
  captureMethod: "Rfid",
  personType: "Personnel",
  personnelId: PERSONNEL_ID,
  personnelNumber: "E-0042",
  personnelName: "Rosa Villanueva",
  ...over,
});

const page = (items: unknown[]) => ({ items, page: 1, pageSize: 200, total: items.length, hasMore: false });

const summaryJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  eventName: "Founders Day",
  expected: 135,
  present: 100,
  late: 5,
  absent: 20,
  excused: 0,
  unexpected: 2,
  attendanceRate: 77.8,
  expectedStudents: 120,
  expectedPersonnel: 15,
  ...over,
});

const audienceJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  status: "Open",
  isFrozen: false,
  expected: 135,
  groups: [],
  students: [],
  definitions: [],
  advisoryPersonnelCount: 15,
  expectedSource: "Audience",
  preRegistration: null,
  expectedStudents: 120,
  expectedPersonnel: 15,
  expectedPersonnelSource: "PreRegistration",
  ...over,
});

const attachResultJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  groupsAttached: 1,
  studentsAttached: 0,
  groupsAlreadyAttached: 0,
  studentsAlreadyAttached: 0,
  definitionsAttached: 0,
  definitionsAlreadyAttached: 0,
  expected: 135,
  warnings: [],
  expectedStudents: 120,
  expectedPersonnel: 15,
  ...over,
});

const reportRowJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  eventName: "Founders Day",
  status: "Closed",
  startAt: "2026-09-01T01:00:00Z",
  expected: 135,
  attended: 105,
  present: 100,
  late: 5,
  absent: 30,
  excused: 0,
  unexpected: 2,
  attendanceRate: 77.8,
  expectedStudents: 120,
  expectedPersonnel: 15,
  ...over,
});

const totalsJson = () => ({
  eventCount: 1,
  expected: 135,
  attended: 105,
  present: 100,
  late: 5,
  absent: 30,
  excused: 0,
  unexpected: 2,
  attendanceRate: 77.8,
});

const scanLogJson = (scans: unknown[]) => ({
  eventId: EVENT_ID,
  totalScans: scans.length,
  distinctCards: scans.length,
  scans,
});

const scanJson = (over: Record<string, unknown> = {}) => ({
  cardUid: "04A1B2C3",
  scannedAt: "2026-09-01T01:07:00Z",
  recordedAt: "2026-09-01T01:07:02Z",
  deviceTapId: null,
  deviceId: null,
  serverOutcome: "CardNotFound",
  localOutcome: null,
  personnelId: null,
  reason: null,
  ...over,
});

// ---------------------------------------------------------------------------------------------
// F1-1 — attendance rows (GET /attendance, AttendanceDtoV2)
// ---------------------------------------------------------------------------------------------

describe("GET /attendance reads the v2 record", () => {
  it("parses a personnel attendance row from GET /attendance with null student fields", async () => {
    serve(() => json(200, page([personnelRow()])));

    const [record] = await api.listAttendance(EVENT_ID);

    expect(record.personType).toBe("Personnel");
    expect(record.personnelId).toBe(PERSONNEL_ID);
    expect(record.personnelNumber).toBe("E-0042");
    expect(record.personnelName).toBe("Rosa Villanueva");
    // Null on the wire means absent here, not an empty string a screen would print.
    expect(record.studentId).toBeUndefined();
    expect(record.studentName).toBeUndefined();
    expect(record.studentNumber).toBeUndefined();
  });

  it("parses a student row unchanged with personType Student", async () => {
    serve(() => json(200, page([studentRow()])));

    const [record] = await api.listAttendance(EVENT_ID);

    expect(record).toEqual({
      id: "a1",
      eventId: EVENT_ID,
      studentId: STUDENT_ID,
      studentName: "Maria Santos",
      studentNumber: "2026-0001",
      personType: "Student",
      personnelId: undefined,
      personnelNumber: undefined,
      personnelName: undefined,
      checkInAt: "2026-09-01T01:05:00Z",
      checkOutAt: undefined,
      status: "Present",
      captureMethod: "Rfid",
    });
  });

  it("fails loud on a personType this build does not know", async () => {
    serve(() => json(200, page([studentRow({ personType: "Visitor" })])));

    const error = await apiErrorFrom(() => api.listAttendance(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("personType");
    expect(error.message).toContain("Visitor");
  });

  it("fails loud when personType is missing, rather than defaulting a row to Student", async () => {
    const { personType: _dropped, ...withoutType } = studentRow();
    serve(() => json(200, page([withoutType])));

    const error = await apiErrorFrom(() => api.listAttendance(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("personType");
  });
});

// ---------------------------------------------------------------------------------------------
// F1-1 — the expected split
// ---------------------------------------------------------------------------------------------

describe("the expected split", () => {
  it("reads expectedStudents and expectedPersonnel off the summary, with expected as the total", async () => {
    serve(() => json(200, summaryJson()));

    const summary = await api.eventSummary(EVENT_ID);

    expect(summary?.expected).toBe(135);
    expect(summary?.expectedStudents).toBe(120);
    expect(summary?.expectedPersonnel).toBe(15);
  });

  it.each(["expectedStudents", "expectedPersonnel"])(
    "fails loud when %s is missing from the summary",
    async (field) => {
      const body: Record<string, unknown> = summaryJson();
      delete body[field];
      serve(() => json(200, body));

      const error = await apiErrorFrom(() => api.eventSummary(EVENT_ID));

      expect(error.kind).toBe("malformed");
      expect(error.message).toContain(field);
    },
  );

  it("reads expectedStudents, expectedPersonnel and expectedPersonnelSource off the audience read", async () => {
    serve(() => json(200, audienceJson()));

    const audience = await api.getEventAudience(EVENT_ID);

    expect(audience?.expected).toBe(135);
    expect(audience?.expectedStudents).toBe(120);
    expect(audience?.expectedPersonnel).toBe(15);
    expect(audience?.expectedSource).toBe("Audience");
    expect(audience?.expectedPersonnelSource).toBe("PreRegistration");
  });

  it.each(["PreRegistration", "Audience"])("accepts %s as the personnel source", async (source) => {
    serve(() => json(200, audienceJson({ expectedPersonnelSource: source })));

    expect((await api.getEventAudience(EVENT_ID))?.expectedPersonnelSource).toBe(source);
  });

  it("fails loud on an expectedPersonnelSource outside PreRegistration and Audience", async () => {
    serve(() => json(200, audienceJson({ expectedPersonnelSource: "Roster" })));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedPersonnelSource");
  });

  it("fails loud when expectedPersonnelSource is missing, rather than copying the student source", async () => {
    const { expectedPersonnelSource: _dropped, ...without } = audienceJson();
    serve(() => json(200, without));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedPersonnelSource");
  });

  it("reads the expected split off the attach result", async () => {
    serve(() => json(200, attachResultJson()));

    const result = await api.attachEventAudience(EVENT_ID, { studentGroupIds: ["g-1"] });

    expect(result.expected).toBe(135);
    expect(result.expectedStudents).toBe(120);
    expect(result.expectedPersonnel).toBe(15);
  });

  it("fails loud, as a write, when the attach result drops expectedPersonnel", async () => {
    const { expectedPersonnel: _dropped, ...without } = attachResultJson();
    serve(() => json(200, without));

    const error = await apiErrorFrom(() => api.attachEventAudience(EVENT_ID, { studentGroupIds: ["g-1"] }));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedPersonnel");
    expect(error.shape).toBe("write");
  });

  it("reads the expected split off each report row (totals carry none)", async () => {
    serve(() => json(200, { events: [reportRowJson()], totals: totalsJson() }));

    const report = await api.multiEventReportSummary([EVENT_ID]);

    expect(report.events[0].expectedStudents).toBe(120);
    expect(report.events[0].expectedPersonnel).toBe(15);
    // The pooled totals are the server's alone: the split is not invented by summing rows.
    expect("expectedStudents" in report.totals).toBe(false);
    expect("expectedPersonnel" in report.totals).toBe(false);
    expect(report.totals.expected).toBe(135);
  });

  it("fails loud when a report row drops expectedPersonnel", async () => {
    const { expectedPersonnel: _dropped, ...without } = reportRowJson();
    serve(() => json(200, { events: [without], totals: totalsJson() }));

    const error = await apiErrorFrom(() => api.multiEventReportSummary([EVENT_ID]));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedPersonnel");
  });
});

// ---------------------------------------------------------------------------------------------
// F1-1 — scans
// ---------------------------------------------------------------------------------------------

describe("GET /events/{id}/scans reads personnelId and reason", () => {
  it("parses personnelId and reason on a scan, null when absent", async () => {
    serve(() =>
      json(
        200,
        scanLogJson([
          scanJson({
            serverOutcome: "CardNotFound",
            personnelId: PERSONNEL_ID,
            reason: "PersonnelClientNotCapable",
          }),
          scanJson({ cardUid: "04FFEEDD" }),
        ]),
      ),
    );

    const log = await api.getEventScans(EVENT_ID);

    expect(log?.scans[0].personnelId).toBe(PERSONNEL_ID);
    expect(log?.scans[0].reason).toBe("PersonnelClientNotCapable");
    // A card that resolved to nobody carries neither, and that is not an error.
    expect(log?.scans[1].personnelId).toBeUndefined();
    expect(log?.scans[1].reason).toBeUndefined();
  });

  it("fails loud when reason is a number rather than a string or null", async () => {
    serve(() => json(200, scanLogJson([scanJson({ reason: 7 })])));

    const error = await apiErrorFrom(() => api.getEventScans(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("reason");
  });
});

// ---------------------------------------------------------------------------------------------
// F1-6 — 503 WriteLockUnavailable + Retry-After on the five lock routes
// ---------------------------------------------------------------------------------------------

const PERSONNEL_REQUEST: PersonnelWriteRequest = {
  personnelNumber: "E-0042",
  firstName: "Rosa",
  middleName: null,
  lastName: "Villanueva",
  email: null,
  classification: null,
  department: null,
  organization: null,
  position: null,
  rfidUid: "04A1B2C3",
  status: "Active",
};

const lockRefusal = (headers: Record<string, string>) =>
  json(
    503,
    {
      status: 503,
      title: "The write could not be completed. Try again shortly.",
      detail: "Another change to the same card was in progress.",
      code: "WriteLockUnavailable",
      traceId: "trace-1",
    },
    headers,
  );

const LOCK_ROUTES: readonly {
  readonly route: string;
  readonly call: () => Promise<unknown>;
  /** FX-2: the two import routes commit part-way; the other three are one transaction. */
  readonly partial: boolean;
}[] = [
  { route: "createPersonnel", call: () => api.createPersonnel(PERSONNEL_REQUEST), partial: false },
  { route: "updatePersonnel", call: () => api.updatePersonnel(PERSONNEL_ID, PERSONNEL_REQUEST), partial: false },
  { route: "importPersonnel", call: () => api.importPersonnel([PERSONNEL_REQUEST]), partial: true },
  { route: "runImport (SIS roster run)", call: () => api.runImport(BATCH_ID, { termId: TERM_ID }), partial: true },
  {
    route: "addStudentCard",
    call: () => api.addStudentCard(STUDENT_ID, { cardUid: "04A1B2C3", label: null }),
    partial: false,
  },
];

describe("503 WriteLockUnavailable carries Retry-After onto the ApiError", () => {
  it.each(LOCK_ROUTES)("$route: carries Retry-After seconds on the ApiError", async ({ call }) => {
    serve(() => lockRefusal({ "Retry-After": "5" }));

    const error = await apiErrorFrom(call);

    expect(error.kind).toBe("http");
    expect(error.status).toBe(503);
    expect(error.code).toBe("WriteLockUnavailable");
    expect(error.shape).toBe("write");
    expect(error.retryAfterSeconds).toBe(5);
  });

  // FX-1: `describeApiError` alone is what several screens render, so the wait has to be in the message.
  it.each(LOCK_ROUTES)("$route: puts 'Try again in 5 seconds.' into the message itself", async ({ call }) => {
    serve(() => lockRefusal({ "Retry-After": "5" }));

    const error = await apiErrorFrom(call);

    expect(error.message).toContain("Try again in 5 seconds.");
  });

  it("says 'Try again shortly.' in the message when Retry-After is missing", async () => {
    serve(() => lockRefusal({}));

    const error = await apiErrorFrom(() => api.createPersonnel(PERSONNEL_REQUEST));

    expect(error.message).toContain("Try again shortly.");
  });

  it("adds no wait to a bare 503, which proves nothing about a lock", async () => {
    serve(() => json(503, { status: 503, title: "down" }, { "Retry-After": "5" }));

    const error = await apiErrorFrom(() => api.createPersonnel(PERSONNEL_REQUEST));

    expect(error.message).not.toContain("Try again");
  });

  // FX-2: the mark is set by the route, at the seam.
  it.each(LOCK_ROUTES)("$route: marks mayCommitPartially only on the import routes", async ({ call, partial }) => {
    serve(() => lockRefusal({ "Retry-After": "5" }));

    const error = await apiErrorFrom(call);

    expect(error.mayCommitPartially).toBe(partial);
  });

  it("leaves retryAfterSeconds undefined when the header is missing", async () => {
    serve(() => lockRefusal({}));

    const error = await apiErrorFrom(() => api.createPersonnel(PERSONNEL_REQUEST));

    expect(error.retryAfterSeconds).toBeUndefined();
  });

  it.each(["soon", "Wed, 21 Oct 2026 07:28:00 GMT", "-3", "1.5", ""])(
    "leaves retryAfterSeconds undefined when the header is %j rather than a whole number of seconds",
    async (header) => {
      serve(() => lockRefusal({ "Retry-After": header }));

      const error = await apiErrorFrom(() => api.createPersonnel(PERSONNEL_REQUEST));

      expect(error.retryAfterSeconds).toBeUndefined();
    },
  );
});
