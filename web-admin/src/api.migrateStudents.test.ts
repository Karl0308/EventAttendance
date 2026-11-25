/** @vitest-environment happy-dom */

// `api.migrateStudentsToPersonnel` — `POST /personnel/migrate-from-students`. The reply is read with the same
// fail-loud style as the other parsers: a drifted outcome or conflict code throws rather than rendering blank.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "./api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const sent: { url: string; body: unknown }[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    sent.push({ url: String(input), body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined });
    return Promise.resolve(reply());
  });
}

afterEach(() => {
  sent.length = 0;
  vi.unstubAllGlobals();
});

const ROW = {
  studentId: "11111111-1111-1111-1111-111111111111",
  studentNumber: "72001",
  fullName: "Cruz, Ana",
  rfidUid: "0012503326",
  outcome: "Create",
  conflictCode: null,
  message: "Will be created.",
  personnelId: null,
};
const BODY = { dryRun: true, prefix: "7200", matched: 1, created: 1, updated: 0, conflicts: 0, rows: [ROW] };

const failure = (p: Promise<unknown>) =>
  p.then(
    () => undefined,
    (thrown: unknown) => thrown,
  );

describe("api.migrateStudentsToPersonnel", () => {
  it("posts the request and parses the plan, mapping null to unset", async () => {
    serve(() => json(200, BODY));

    const result = await api.migrateStudentsToPersonnel({ prefix: "7200", dryRun: true });

    expect(sent[0].url).toContain("/personnel/migrate-from-students");
    expect(sent[0].body).toEqual({ prefix: "7200", dryRun: true });
    expect(result.matched).toBe(1);
    expect(result.rows[0]).toEqual({
      studentId: ROW.studentId,
      studentNumber: "72001",
      fullName: "Cruz, Ana",
      rfidUid: "0012503326",
      outcome: "Create",
      conflictCode: undefined,
      message: "Will be created.",
      personnelId: undefined,
    });
  });

  it("reads a Conflict row with its code", async () => {
    serve(() =>
      json(200, { ...BODY, created: 0, conflicts: 1, rows: [{ ...ROW, outcome: "Conflict", conflictCode: "ActiveEvent", rfidUid: null }] }),
    );

    const result = await api.migrateStudentsToPersonnel({ dryRun: true });

    expect(result.rows[0].outcome).toBe("Conflict");
    expect(result.rows[0].conflictCode).toBe("ActiveEvent");
    expect(result.rows[0].rfidUid).toBeUndefined();
  });

  it("throws on an unknown outcome rather than rendering a blank chip", async () => {
    serve(() => json(200, { ...BODY, rows: [{ ...ROW, outcome: "Skipped" }] }));

    const thrown = await failure(api.migrateStudentsToPersonnel({ dryRun: true }));

    expect(thrown).toBeInstanceOf(ApiError);
    expect((thrown as ApiError).kind).toBe("malformed");
  });

  it("throws on an unknown conflict code, and on a Conflict with none", async () => {
    serve(() => json(200, { ...BODY, rows: [{ ...ROW, outcome: "Conflict", conflictCode: "Mystery" }] }));
    expect(((await failure(api.migrateStudentsToPersonnel({ dryRun: true }))) as ApiError).kind).toBe("malformed");

    serve(() => json(200, { ...BODY, rows: [{ ...ROW, outcome: "Conflict", conflictCode: null }] }));
    expect(((await failure(api.migrateStudentsToPersonnel({ dryRun: true }))) as ApiError).kind).toBe("malformed");
  });

  it("throws on a missing count", async () => {
    const { matched: _matched, ...withoutMatched } = BODY;
    serve(() => json(200, withoutMatched));

    const thrown = await failure(api.migrateStudentsToPersonnel({ dryRun: true }));

    expect((thrown as ApiError).kind).toBe("malformed");
  });
});
