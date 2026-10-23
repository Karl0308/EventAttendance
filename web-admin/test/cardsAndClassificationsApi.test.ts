// `searchCards` — `GET /cards?cardUid=` — narrowed at the seam like everything else in this file, and
// pinned here the way `deviceKeyIssued.test.ts` pins `toDeviceKeyIssued`: `fetch` stubbed, the public
// `api.*` call made, the request and the mapped reply both asserted.
//
// (The file also pinned `listClassifications`, `assignClassification` and `clearClassification` until
// student classification was withdrawn from the product surface — ADR-009, D-76. Those methods and
// their types left `api.ts` with the dialog that used them.)
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// 1. **The request shape.** `GET /cards?cardUid=` carries the fragment and pages like every other
//    list.
// 2. **The narrowing.** Every required field on the DTO this reads is `reqStr`/`reqBool` checked, so a
//    server that drops one fails loud here — `malformed`, `shape: "read"` — rather than arriving at a
//    dialog as `undefined`.

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
