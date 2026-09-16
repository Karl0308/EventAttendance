// The four new methods on `src/api.ts` this slice added — `searchCards`, `listClassifications`,
// `assignClassification`, `clearClassification` — narrowed at the seam like everything else in this
// file, and pinned here the way `deviceKeyIssued.test.ts` pins `toDeviceKeyIssued`: `fetch` stubbed,
// the public `api.*` call made, the request and the mapped reply both asserted.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// 1. **The request shape.** `GET /cards?cardUid=` carries the fragment and pages like every other
//    list; the two write methods carry `PUT`/`DELETE` to the right URL, with ids
//    `encodeURIComponent`-ed rather than concatenated raw.
// 2. **The narrowing.** Every required field on the four DTOs this slice reads is `reqStr`/`reqBool`
//    checked, so a server that drops one fails loud here — `malformed`, `shape: "read"` — rather
//    than arriving at a dialog as `undefined`. `SUPERVISORY/MANAGERIAL` gets its own test: a name
//    with a slash in it is exactly the value most likely to break a naive split somewhere between
//    the wire and the chip that renders it.
// 3. **`shape` on the two writes.** `assignClassification`/`clearClassification` go through
//    `writeJson`, so a failure — including a 2xx reply that will not narrow — must raise
//    `shape: "write"`, never `"read"`. Getting that backwards is the defect class `ApiRequestShape`'s
//    own comment describes: a write's failure rendered as though retrying it were free.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** Every request made, so a test can assert on it without re-parsing a mock's call history by hand. */
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

/** The `ApiError` a call raised, so a call that unexpectedly succeeds cannot read as a pass. */
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

// ---------------------------------------------------------------------------------------------
// searchCards — GET /cards?cardUid=
// ---------------------------------------------------------------------------------------------

const cardMatchJson = (over: Record<string, unknown> = {}) => ({
  cardId: "c-1",
  cardUid: "0012503326",
  label: null,
  isActive: true,
  issuedAt: "2025-06-01T00:00:00Z",
  deactivatedAt: null,
  studentId: "s-1",
  studentNumber: "2026000001",
  fullName: "Ana Cruz",
  studentStatus: "Active",
  ...over,
});

describe("api.searchCards", () => {
  it("sends the fragment, the page and the page size, and nothing normalized out of the fragment", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 50, total: 0, hasMore: false }));

    await api.searchCards("0012503326", 1, 50);

    expect(lastRequest().method).toBe("GET");
    expect(lastRequest().url).toMatch(/\/cards\?/);
    expect(lastQuery().get("cardUid")).toBe("0012503326");
    expect(lastQuery().get("page")).toBe("1");
    expect(lastQuery().get("pageSize")).toBe("50");
  });

  it("maps every field of a well-formed match, including the ones that are absent", async () => {
    serve(() =>
      json(200, {
        items: [cardMatchJson()],
        page: 1,
        pageSize: 50,
        total: 1,
        hasMore: false,
      }),
    );

    const page = await api.searchCards("2503326", 1, 50);

    expect(page.total).toBe(1);
    expect(page.cards).toHaveLength(1);
    expect(page.cards[0]).toEqual({
      cardId: "c-1",
      cardUid: "0012503326",
      label: undefined,
      isActive: true,
      issuedAt: "2025-06-01T00:00:00Z",
      deactivatedAt: undefined,
      studentId: "s-1",
      studentNumber: "2026000001",
      fullName: "Ana Cruz",
      studentStatus: "Active",
    });
  });

  it("keeps a withdrawn card's deactivatedAt rather than dropping it", async () => {
    serve(() =>
      json(200, {
        items: [cardMatchJson({ isActive: false, deactivatedAt: "2026-01-15T00:00:00Z" })],
        page: 1,
        pageSize: 50,
        total: 1,
        hasMore: false,
      }),
    );

    const page = await api.searchCards("2503326", 1, 50);

    expect(page.cards[0].isActive).toBe(false);
    expect(page.cards[0].deactivatedAt).toBe("2026-01-15T00:00:00Z");
  });

  it("fails loud, read-shaped, when a required field is missing rather than defaulting it", async () => {
    serve(() =>
      json(200, {
        items: [
          {
            cardId: "c-1",
            cardUid: "0012503326",
            // `isActive` missing. QA Q5 turns on this field, so a silent default here is the one
            // drift that would matter most.
            issuedAt: "2025-06-01T00:00:00Z",
            studentId: "s-1",
            studentNumber: "2026000001",
            fullName: "Ana Cruz",
            studentStatus: "Active",
          },
        ],
        page: 1,
        pageSize: 50,
        total: 1,
        hasMore: false,
      }),
    );

    const error = await apiErrorFrom(() => api.searchCards("2503326", 1, 50));

    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("read");
    expect(error.message).toMatch(/isActive/);
  });

  it("raises the 400 as ApiError with the FragmentUnusable code, read-shaped", async () => {
    serve(() =>
      json(400, {
        status: 400,
        title: "The request could not be processed.",
        detail: "The search fragment does not contain any letters or digits.",
        code: "FragmentUnusable",
      }),
    );

    const error = await apiErrorFrom(() => api.searchCards("-", 1, 50));

    expect(error.status).toBe(400);
    expect(error.code).toBe("FragmentUnusable");
    expect(error.shape).toBe("read");
  });
});

// ---------------------------------------------------------------------------------------------
// listClassifications — GET /classifications
// ---------------------------------------------------------------------------------------------

const classificationJson = (over: Record<string, unknown> = {}) => ({
  id: "v-1",
  name: "NAP",
  nameKey: "nap",
  axis: "Student",
  isActive: true,
  retiredAt: null,
  mergedIntoClassificationId: null,
  studentCount: 2,
  ...over,
});

describe("api.listClassifications", () => {
  it("omits includeRetired when not asked for, rather than sending it as the string \"undefined\"", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));

    await api.listClassifications();

    expect(lastQuery().has("includeRetired")).toBe(false);
  });

  it("sends includeRetired as the literal string true when asked for", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 200, total: 0, hasMore: false }));

    await api.listClassifications(true);

    expect(lastQuery().get("includeRetired")).toBe("true");
  });

  it("carries SUPERVISORY/MANAGERIAL through the mapper with the slash intact", async () => {
    serve(() =>
      json(200, {
        items: [
          classificationJson({
            id: "v-supervisory",
            name: "SUPERVISORY/MANAGERIAL",
            nameKey: "supervisory-managerial",
            axis: "Personnel",
          }),
        ],
        page: 1,
        pageSize: 200,
        total: 1,
        hasMore: false,
      }),
    );

    const list = await api.listClassifications();

    expect(list[0].name).toBe("SUPERVISORY/MANAGERIAL");
  });

  it("maps a retired entry's retiredAt and a merge target, when present", async () => {
    serve(() =>
      json(200, {
        items: [
          classificationJson({
            id: "v-old",
            isActive: false,
            retiredAt: "2025-12-01T00:00:00Z",
            mergedIntoClassificationId: "v-new",
          }),
        ],
        page: 1,
        pageSize: 200,
        total: 1,
        hasMore: false,
      }),
    );

    const list = await api.listClassifications(true);

    expect(list[0].isActive).toBe(false);
    expect(list[0].retiredAt).toBe("2025-12-01T00:00:00Z");
    expect(list[0].mergedIntoClassificationId).toBe("v-new");
  });
});

// ---------------------------------------------------------------------------------------------
// assignClassification — PUT /students/{studentId}/classifications/{classificationId}
// ---------------------------------------------------------------------------------------------

const writeResultJson = (over: Record<string, unknown> = {}) => ({
  studentId: "s-1",
  classifications: [],
  replacedClassificationId: null,
  message: "The classification was assigned.",
  ...over,
});

describe("api.assignClassification", () => {
  it("PUTs to the two-id URL with both ids encoded, and an empty JSON body", async () => {
    serve(() => json(200, writeResultJson()));

    await api.assignClassification("s 1", "c/1");

    expect(lastRequest().method).toBe("PUT");
    expect(lastRequest().url).toContain(
      `/students/${encodeURIComponent("s 1")}/classifications/${encodeURIComponent("c/1")}`,
    );
    expect(lastRequest().body).toBe("{}");
  });

  it("maps the whole set the server returns, and replacedClassificationId when it displaced something", async () => {
    serve(() =>
      json(200, {
        studentId: "s-1",
        classifications: [
          {
            classificationId: "v-nap",
            name: "NAP",
            axis: "Student",
            isActive: true,
            assignedAt: "2026-01-01T00:00:00Z",
          },
        ],
        replacedClassificationId: "v-student",
        message: "The classification was assigned, replacing STUDENT.",
      }),
    );

    const result = await api.assignClassification("s-1", "v-nap");

    expect(result.classifications).toHaveLength(1);
    expect(result.classifications[0].name).toBe("NAP");
    expect(result.replacedClassificationId).toBe("v-student");
  });

  it("leaves replacedClassificationId undefined when nothing was displaced", async () => {
    serve(() => json(200, writeResultJson()));

    const result = await api.assignClassification("s-1", "v-nap");

    expect(result.replacedClassificationId).toBeUndefined();
  });

  it("raises a 409 write-shaped, so advise() offers the retry rather than the duplicate warning", async () => {
    serve(() =>
      json(409, {
        status: 409,
        title: "Conflict.",
        detail: "This classification has been retired since this page was opened.",
        code: "ClassificationRetired",
      }),
    );

    const error = await apiErrorFrom(() => api.assignClassification("s-1", "v-old"));

    expect(error.status).toBe(409);
    expect(error.shape).toBe("write");
  });

  it("raises a 2xx-but-unreadable reply as malformed and write-shaped, never as read-shaped", async () => {
    // The one failure mode `writeJson`'s own comment calls out by name: the server accepted the
    // write and this build cannot read back what it did. `shape` must stay "write" so the caller is
    // told to check the list rather than offered a free-looking retry.
    serve(() => json(200, { studentId: "s-1" /* classifications, message missing */ }));

    const error = await apiErrorFrom(() => api.assignClassification("s-1", "v-nap"));

    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("write");
  });
});

// ---------------------------------------------------------------------------------------------
// clearClassification — DELETE /students/{studentId}/classifications/{classificationId}
// ---------------------------------------------------------------------------------------------

describe("api.clearClassification", () => {
  it("DELETEs to the two-id URL with both ids encoded, and no body", async () => {
    serve(() => json(200, writeResultJson()));

    await api.clearClassification("s 1", "c/1");

    expect(lastRequest().method).toBe("DELETE");
    expect(lastRequest().url).toContain(
      `/students/${encodeURIComponent("s 1")}/classifications/${encodeURIComponent("c/1")}`,
    );
    expect(lastRequest().body).toBeUndefined();
  });

  it("maps the remaining classifications from the 200 body, replacedClassificationId always undefined", async () => {
    serve(() =>
      json(200, {
        studentId: "s-1",
        classifications: [
          {
            classificationId: "v-acad",
            name: "ACAD",
            axis: "Personnel",
            isActive: true,
            assignedAt: "2026-01-01T00:00:00Z",
          },
        ],
        // A DELETE never sends this field. Omitted here, as the real endpoint omits it.
        message: "The classification was cleared.",
      }),
    );

    const result = await api.clearClassification("s-1", "v-nap");

    expect(result.classifications).toHaveLength(1);
    expect(result.classifications[0].axis).toBe("Personnel");
    expect(result.replacedClassificationId).toBeUndefined();
  });

  it("raises a 409 write-shaped, the same as the assign side", async () => {
    serve(() =>
      json(409, {
        status: 409,
        title: "Conflict.",
        detail: "Somebody else changed this axis a moment ago.",
        code: "ConcurrentClassificationChange",
      }),
    );

    const error = await apiErrorFrom(() => api.clearClassification("s-1", "v-nap"));

    expect(error.status).toBe(409);
    expect(error.shape).toBe("write");
  });
});
