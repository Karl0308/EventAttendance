// Task 2 Phase 2: the `src/api.ts` half of "Add audience" — reusable audience definitions attached to an
// event beside its sections.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The contract the backend is being built against in parallel, field name for field name:
//
//   POST   /events/{id}/attendees                       body gains `audienceDefinitionIds`; reply gains
//                                                       `definitionsAttached` / `definitionsAlreadyAttached`
//   GET    /events/{id}/attendees                       gains `definitions[]` and `advisoryPersonnelCount`
//   DELETE /events/{id}/attendees/definitions/{defId}   detach one
//   GET    /event-audiences?eventClassificationId=      the picker's options
//   GET    /events/{id}                                 `eventClassificationId` / `eventClassificationName`
//
// Every new field is *required* at the seam, so a server that drops one fails loud (`malformed`) rather
// than arriving at a panel as `undefined` — and in particular an advisory that silently read as 0 would
// hide eligible personnel.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";
const DEFINITION_ID = "55555555-5555-4555-8555-555555555555";
const CLASSIFICATION_ID = "66666666-6666-4666-8666-666666666666";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let asked: { method: string; url: string; body: unknown }[] = [];

function serve(reply: (method: string, url: string) => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    asked.push({ method, url, body: init?.body });
    return Promise.resolve(reply(method, url));
  });
}

const lastRequest = () => asked[asked.length - 1];
const lastQuery = () => new URL(lastRequest().url, "http://localhost").searchParams;

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
  asked = [];
  vi.unstubAllGlobals();
});

const definitionJson = (over: Record<string, unknown> = {}) => ({
  audienceDefinitionId: DEFINITION_ID,
  name: "All Freshmen",
  audienceType: "YearLevel",
  studentCount: 120,
  personnelCount: 7,
  isActive: true,
  ...over,
});

const audienceJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  status: "Draft",
  isFrozen: false,
  expected: 120,
  groups: [],
  students: [],
  definitions: [definitionJson()],
  advisoryPersonnelCount: 7,
  expectedSource: "Audience",
  preRegistration: null,
  ...over,
});

const resultJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  groupsAttached: 0,
  studentsAttached: 0,
  groupsAlreadyAttached: 0,
  studentsAlreadyAttached: 0,
  definitionsAttached: 1,
  definitionsAlreadyAttached: 2,
  expected: 120,
  warnings: [],
  ...over,
});

describe("api.attachEventAudience with audience definitions", () => {
  it("sends audienceDefinitionIds and parses definitionsAttached / definitionsAlreadyAttached", async () => {
    serve(() => json(200, resultJson()));

    const result = await api.attachEventAudience(EVENT_ID, { audienceDefinitionIds: [DEFINITION_ID] });

    expect(lastRequest().method).toBe("POST");
    expect(new URL(lastRequest().url, "http://localhost").pathname).toMatch(
      new RegExp(`/events/${EVENT_ID}/attendees$`),
    );
    expect(JSON.parse(String(lastRequest().body))).toEqual({ audienceDefinitionIds: [DEFINITION_ID] });
    expect(result.definitionsAttached).toBe(1);
    expect(result.definitionsAlreadyAttached).toBe(2);
    expect(result.expected).toBe(120);
  });

  it("fails loud, as a write, when the reply drops a definitions counter", async () => {
    const { definitionsAlreadyAttached: _dropped, ...withoutCounter } = resultJson();
    serve(() => json(200, withoutCounter));

    const error = await apiErrorFrom(() =>
      api.attachEventAudience(EVENT_ID, { audienceDefinitionIds: [DEFINITION_ID] }),
    );

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("definitionsAlreadyAttached");
    // A write's malformed reply must never read as "retrying is free".
    expect(error.shape).toBe("write");
  });
});

describe("api.getEventAudience reads definitions and the advisory", () => {
  it("parses definitions[] and advisoryPersonnelCount", async () => {
    serve(() => json(200, audienceJson()));

    const audience = await api.getEventAudience(EVENT_ID);

    expect(audience?.advisoryPersonnelCount).toBe(7);
    expect(audience?.definitions).toEqual([
      {
        audienceDefinitionId: DEFINITION_ID,
        name: "All Freshmen",
        audienceType: "YearLevel",
        studentCount: 120,
        personnelCount: 7,
        isActive: true,
      },
    ]);
  });

  it("fails loud on a malformed definitions element, naming it", async () => {
    serve(() => json(200, audienceJson({ definitions: [definitionJson({ studentCount: "120" })] })));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("definitions[0]");
    expect(error.message).toContain("studentCount");
  });

  it("fails loud when definitions is not an array", async () => {
    serve(() => json(200, audienceJson({ definitions: null })));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("definitions");
  });

  it("fails loud when advisoryPersonnelCount is missing, rather than reading it as zero", async () => {
    const { advisoryPersonnelCount: _dropped, ...withoutAdvisory } = audienceJson();
    serve(() => json(200, withoutAdvisory));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("advisoryPersonnelCount");
  });
});

describe("api.detachAudienceDefinition", () => {
  it("sends DELETE to the definitions sub-resource, bodyless, with the ids encoded", async () => {
    serve(() => new Response(null, { status: 204 }));

    await api.detachAudienceDefinition(EVENT_ID, DEFINITION_ID);

    expect(lastRequest().method).toBe("DELETE");
    expect(new URL(lastRequest().url, "http://localhost").pathname).toMatch(
      new RegExp(`/events/${EVENT_ID}/attendees/definitions/${DEFINITION_ID}$`),
    );
    expect(lastRequest().body).toBeUndefined();
  });
});

describe("api.listAudienceDefinitions and the event's classification", () => {
  it("filters by eventClassificationId and asks for active definitions only", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));

    await api.listAudienceDefinitions({ eventClassificationId: CLASSIFICATION_ID, includeInactive: false });

    expect(lastQuery().get("eventClassificationId")).toBe(CLASSIFICATION_ID);
    expect(lastQuery().get("includeInactive")).toBe("false");
  });

  it("reads eventClassificationId / eventClassificationName off the event, null as unset", async () => {
    const eventJson = (over: Record<string, unknown>) => ({
      id: EVENT_ID,
      name: "Orientation",
      description: null,
      location: null,
      requireRegistration: false,
      startAt: "2026-09-01T01:00:00Z",
      endAt: "2026-09-01T05:00:00Z",
      attendanceMode: "Single",
      graceMinutes: 15,
      status: "Draft",
      issuesCertificates: false,
      ...over,
    });

    serve((_method, url) =>
      new URL(url, "http://localhost").pathname.endsWith(`/events/${EVENT_ID}`)
        ? json(200, eventJson({ eventClassificationId: CLASSIFICATION_ID, eventClassificationName: "Institutional" }))
        : json(404, {}),
    );
    const classified = await api.getEvent(EVENT_ID);
    expect(classified?.eventClassificationId).toBe(CLASSIFICATION_ID);
    expect(classified?.eventClassificationName).toBe("Institutional");

    serve(() => json(200, eventJson({ eventClassificationId: null, eventClassificationName: null })));
    const unclassified = await api.getEvent(EVENT_ID);
    expect(unclassified?.eventClassificationId).toBeUndefined();
    expect(unclassified?.eventClassificationName).toBeUndefined();
  });
});
