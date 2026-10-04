/** @vitest-environment happy-dom */

// `api.listPersonnelOrganizations` — `GET /personnel/organizations` returns a BARE JSON array of strings
// (Task 2 D2-org). It is read with `getJson` and narrowed element by element, so a drifted reply fails
// loudly instead of rendering `[object Object]` in the Organization dropdown.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "./api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let asked: { method: string; url: string }[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    asked.push({ method: init?.method ?? "GET", url: String(input) });
    return Promise.resolve(reply());
  });
}

afterEach(() => {
  asked = [];
  vi.unstubAllGlobals();
});

describe("api.listPersonnelOrganizations", () => {
  it("reads a bare string array", async () => {
    serve(() => json(200, ["A", "B"]));

    const organizations = await api.listPersonnelOrganizations();

    expect(organizations).toEqual(["A", "B"]);
    expect(asked).toHaveLength(1);
    expect(asked[0].method).toBe("GET");
    expect(asked[0].url).toContain("/personnel/organizations");
  });

  it("throws on a non-string element", async () => {
    serve(() => json(200, ["A", 1]));

    const failure = await api.listPersonnelOrganizations().then(
      () => undefined,
      (thrown: unknown) => thrown,
    );

    expect(failure).toBeInstanceOf(ApiError);
    const error = failure as ApiError;
    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("read");
    expect(error.message).toContain("[1]");
    expect(error.message).toContain("string");
  });
});
