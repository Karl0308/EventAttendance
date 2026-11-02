/** @vitest-environment happy-dom */

// `api.listPersonnelPicker` — `GET /events/personnel-picker` (B1 gate fix). A paged envelope of exactly
// `{ id, personnelNumber, fullName }`, read through `listAll`; members beyond those three are dropped.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "./api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const urls: string[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    urls.push(String(input));
    return Promise.resolve(reply());
  });
}

afterEach(() => {
  urls.length = 0;
  vi.unstubAllGlobals();
});

describe("api.listPersonnelPicker", () => {
  it("reads the picker route and keeps only the three fields", async () => {
    serve(() =>
      json(200, {
        items: [{ id: "p-1", personnelNumber: "P-001", fullName: "Santos, Maria", email: "leak@usa.edu.ph" }],
        page: 1,
        pageSize: 200,
        total: 1,
        hasMore: false,
      }),
    );

    const rows = await api.listPersonnelPicker();

    expect(rows).toEqual([{ id: "p-1", personnelNumber: "P-001", fullName: "Santos, Maria" }]);
    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain("/events/personnel-picker");
    expect(urls[0]).not.toContain("/personnel?");
  });

  it("throws on a row missing a field rather than rendering a blank option", async () => {
    serve(() =>
      json(200, { items: [{ id: "p-1", personnelNumber: "P-001" }], page: 1, pageSize: 200, total: 1, hasMore: false }),
    );

    const failure = await api.listPersonnelPicker().then(
      () => undefined,
      (thrown: unknown) => thrown,
    );

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).kind).toBe("malformed");
  });
});
