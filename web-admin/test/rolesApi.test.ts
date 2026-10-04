// `api.listRoles` — `GET /roles` returns a BARE JSON array (`IReadOnlyList<RoleDto>`, RolesController),
// not the §6 paged envelope. It used to go through `listAll`/`asPage`, which expects `{ items, ... }`,
// so the Roles page failed with "expected an object, got object" (an array, mis-named by `describeType`).
//
// WHAT THIS FILE PINS
//
// 1. A bare array of role rows maps to `Role[]`.
// 2. A genuinely wrong shape still fails loud — `malformed`, `shape: "read"` — rather than rendering
//    a half-empty page.
// 3. An off-contract message names an array "array", not "object". `describeType` is not exported, so
//    this is asserted through the messages `asRow` / `reqStr` build from it.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let asked: { method: string; url: string }[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    asked.push({ method: init?.method ?? "GET", url: String(input) });
    return Promise.resolve(reply());
  });
}

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

const roleJson = (over: Record<string, unknown> = {}) => ({
  id: "r-1",
  name: "Registrar",
  description: null,
  isSystem: false,
  userCount: 2,
  permissionCodes: ["students.read", "events.read"],
  ...over,
});

afterEach(() => {
  asked = [];
  vi.unstubAllGlobals();
});

describe("api.listRoles", () => {
  it("parses a bare array of role rows into Role[]", async () => {
    serve(() =>
      json(200, [
        roleJson(),
        roleJson({ id: "r-2", name: "SuperAdmin", description: "Everything", isSystem: true, userCount: 1, permissionCodes: [] }),
      ]));

    const roles = await api.listRoles();

    expect(asked).toHaveLength(1);
    expect(asked[0].method).toBe("GET");
    expect(asked[0].url).toMatch(/\/roles$/);
    expect(roles).toEqual([
      { id: "r-1", name: "Registrar", description: undefined, isSystem: false, userCount: 2, permissionCodes: ["students.read", "events.read"] },
      { id: "r-2", name: "SuperAdmin", description: "Everything", isSystem: true, userCount: 1, permissionCodes: [] },
    ]);
  });

  it("returns an empty list for an empty array", async () => {
    serve(() => json(200, []));
    expect(await api.listRoles()).toEqual([]);
  });

  it("fails loud (malformed, read) when the body is not an array", async () => {
    serve(() => json(200, { items: [roleJson()], page: 1, pageSize: 50, total: 1, hasMore: false }));

    const error = await apiErrorFrom(() => api.listRoles());

    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("read");
    expect(error.message).toContain("GET /roles");
    expect(error.message).toContain("expected an array, got object");
  });

  it("fails loud (malformed, read) when a row is missing a required field", async () => {
    serve(() => json(200, [roleJson({ isSystem: undefined })]));

    const error = await apiErrorFrom(() => api.listRoles());

    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("read");
    // Pin the ROW-level field check: on revert, the bare array trips the envelope
    // guard ("expected an array, got object") before any row is read, so without
    // this the test stays green on revert and guards nothing (JJReviewer warning).
    expect(error.message).toContain("isSystem");
  });

  it("names an array 'array', not 'object', in off-contract messages", async () => {
    // A row that is itself an array, and a field that is an array where a string belongs.
    serve(() => json(200, [[]]));
    const asRowError = await apiErrorFrom(() => api.listRoles());
    expect(asRowError.message).toContain("expected an object, got array");

    serve(() => json(200, [roleJson({ name: [] })]));
    const fieldError = await apiErrorFrom(() => api.listRoles());
    expect(fieldError.message).toContain("`name` should be a string, got array");
  });
});
