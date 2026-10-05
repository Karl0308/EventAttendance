// Task 2 Phase 3: the `src/api.ts` half of "link a pre-registration session to an event".
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The contract the backend is being built against in parallel, field name for field name:
//
//   POST   /events/{id}/pre-registration/sessions                body { preRegistrationSessionId }
//   DELETE /events/{id}/pre-registration/sessions/{sessionId}
//   POST   /events/{id}/pre-registration/sessions/from-event     body { name? }
//   GET    /events/{id}/attendees  gains `expectedSource` ("PreRegistration" | "Audience") and
//                                  `preRegistration` ({ linkedSessions[], totals } | null)
//
// `expectedSource` and `preRegistration` are required at the seam: an event whose denominator is the
// pre-registered students must never arrive at the screen labelled as coming from the audience.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const EVENT_ID = "44444444-4444-4444-8444-444444444444";
const SESSION_ID = "77777777-7777-4777-8777-777777777777";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let asked: { method: string; url: string; body: unknown }[] = [];

function serve(reply: (method: string, url: string) => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    asked.push({ method, url: String(input), body: init?.body });
    return Promise.resolve(reply(method, String(input)));
  });
}

const lastRequest = () => asked[asked.length - 1];
const lastPath = () => new URL(lastRequest().url, "http://localhost").pathname;

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

const linkedSessionJson = (over: Record<string, unknown> = {}) => ({
  preRegistrationSessionId: SESSION_ID,
  name: "Orientation pre-reg",
  preRegisteredStudentCount: 30,
  advisoryPersonnelCount: 4,
  ...over,
});

const preRegistrationJson = (over: Record<string, unknown> = {}) => ({
  linkedSessions: [linkedSessionJson()],
  totalPreRegisteredStudentCount: 30,
  totalAdvisoryPersonnelCount: 4,
  ...over,
});

const audienceJson = (over: Record<string, unknown> = {}) => ({
  eventId: EVENT_ID,
  status: "Draft",
  isFrozen: false,
  expected: 30,
  groups: [],
  students: [],
  definitions: [],
  advisoryPersonnelCount: 0,
  expectedSource: "PreRegistration",
  preRegistration: preRegistrationJson(),
  ...over,
});

describe("api.linkPreRegistrationSession", () => {
  it("POSTs { preRegistrationSessionId } to the event's pre-registration sessions", async () => {
    serve(() => new Response(null, { status: 204 }));

    await api.linkPreRegistrationSession(EVENT_ID, SESSION_ID);

    expect(lastRequest().method).toBe("POST");
    expect(lastPath()).toMatch(new RegExp(`/events/${EVENT_ID}/pre-registration/sessions$`));
    expect(JSON.parse(String(lastRequest().body))).toEqual({ preRegistrationSessionId: SESSION_ID });
  });

  it("surfaces a 409 as an http ApiError, as a write, rather than swallowing it", async () => {
    serve(() => json(409, { title: "EventLocked", status: 409 }));

    const error = await apiErrorFrom(() => api.linkPreRegistrationSession(EVENT_ID, SESSION_ID));

    expect(error.kind).toBe("http");
    expect(error.status).toBe(409);
    expect(error.shape).toBe("write");
  });
});

describe("api.unlinkPreRegistrationSession", () => {
  it("sends a bodyless DELETE to the session sub-resource", async () => {
    serve(() => new Response(null, { status: 204 }));

    await api.unlinkPreRegistrationSession(EVENT_ID, SESSION_ID);

    expect(lastRequest().method).toBe("DELETE");
    expect(lastPath()).toMatch(new RegExp(`/events/${EVENT_ID}/pre-registration/sessions/${SESSION_ID}$`));
    expect(lastRequest().body).toBeUndefined();
  });
});

describe("api.createPreRegistrationFromEvent", () => {
  it("POSTs { name } to .../sessions/from-event", async () => {
    serve(() => json(201, {}));

    await api.createPreRegistrationFromEvent(EVENT_ID, "Fresh session");

    expect(lastRequest().method).toBe("POST");
    expect(lastPath()).toMatch(new RegExp(`/events/${EVENT_ID}/pre-registration/sessions/from-event$`));
    expect(JSON.parse(String(lastRequest().body))).toEqual({ name: "Fresh session" });
  });

  it("omits name from the body when none, or only whitespace, is given", async () => {
    serve(() => json(201, {}));

    await api.createPreRegistrationFromEvent(EVENT_ID);
    expect(JSON.parse(String(lastRequest().body))).toEqual({});

    await api.createPreRegistrationFromEvent(EVENT_ID, "   ");
    expect(JSON.parse(String(lastRequest().body))).toEqual({});
  });
});

describe("api.getEventAudience reads expectedSource and preRegistration", () => {
  it("parses expectedSource and the linked sessions with their totals", async () => {
    serve(() => json(200, audienceJson()));

    const audience = await api.getEventAudience(EVENT_ID);

    expect(audience?.expectedSource).toBe("PreRegistration");
    expect(audience?.preRegistration).toEqual({
      linkedSessions: [
        {
          preRegistrationSessionId: SESSION_ID,
          name: "Orientation pre-reg",
          preRegisteredStudentCount: 30,
          advisoryPersonnelCount: 4,
        },
      ],
      totalPreRegisteredStudentCount: 30,
      totalAdvisoryPersonnelCount: 4,
    });
  });

  it("reads preRegistration: null as nothing linked, with the Audience source", async () => {
    serve(() => json(200, audienceJson({ expectedSource: "Audience", preRegistration: null })));

    const audience = await api.getEventAudience(EVENT_ID);

    expect(audience?.expectedSource).toBe("Audience");
    expect(audience?.preRegistration).toBeNull();
  });

  it("fails loud on a malformed linked-session element, naming it", async () => {
    serve(() =>
      json(
        200,
        audienceJson({
          preRegistration: preRegistrationJson({
            linkedSessions: [linkedSessionJson({ preRegisteredStudentCount: "30" })],
          }),
        }),
      ),
    );

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("preRegistration.linkedSessions[0]");
    expect(error.message).toContain("preRegisteredStudentCount");
  });

  it("fails loud when expectedSource is missing, rather than defaulting it to Audience", async () => {
    const { expectedSource: _dropped, ...withoutSource } = audienceJson();
    serve(() => json(200, withoutSource));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedSource");
  });

  it("fails loud on an expectedSource this build does not know", async () => {
    serve(() => json(200, audienceJson({ expectedSource: "Roster" })));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("expectedSource");
  });

  it("fails loud when preRegistration is missing, rather than reading it as nothing linked", async () => {
    const { preRegistration: _dropped, ...withoutBlock } = audienceJson();
    serve(() => json(200, withoutBlock));

    const error = await apiErrorFrom(() => api.getEventAudience(EVENT_ID));

    expect(error.kind).toBe("malformed");
    expect(error.message).toContain("preRegistration");
  });
});
