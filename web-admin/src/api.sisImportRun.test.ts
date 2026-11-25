/** @vitest-environment happy-dom */

// `api.runImport` on the 409s `POST /sis/import/{batchId}/run` draws. Only `ImportAlreadyRunning`
// carries `code` and `runningBatchId`; the other 409s (term mismatch, batch already ran) must reach the
// screen as ordinary refusals, and the screen tells them apart through `importAlreadyRunningOf`.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, IMPORT_ALREADY_RUNNING_CODE, api } from "./api";
import { importAlreadyRunningOf } from "./sisImport";

const BATCH_ID = "11111111-1111-4111-8111-111111111111";
const RUNNING_ID = "0b6f5d3e-8c1a-4d52-9f7e-3a1c2b4d5e6f";
const TERM_ID = "22222222-2222-4222-8222-222222222222";

const problemJson = (body: unknown) =>
  new Response(JSON.stringify(body), {
    status: 409,
    headers: { "Content-Type": "application/problem+json" },
  });

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", () => Promise.resolve(reply()));
}

async function runRefused(): Promise<unknown> {
  return api.runImport(BATCH_ID, { termId: TERM_ID }).then(
    () => undefined,
    (thrown: unknown) => thrown,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("api.runImport 409 handling", () => {
  it("carries the code and the running batch id off the wire", async () => {
    serve(() =>
      problemJson({
        status: 409,
        title: "Another import is already running.",
        detail: "Batch 0b6f5d3e is running. This batch was not changed.",
        code: IMPORT_ALREADY_RUNNING_CODE,
        runningBatchId: RUNNING_ID,
      }),
    );

    const failure = await runRefused();

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).code).toBe(IMPORT_ALREADY_RUNNING_CODE);
    expect(importAlreadyRunningOf(failure)).toEqual({
      detail: "Batch 0b6f5d3e is running. This batch was not changed.",
      runningBatchId: RUNNING_ID,
    });
  });

  it("ignores a runningBatchId that is not a string", async () => {
    serve(() =>
      problemJson({ status: 409, code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: { id: RUNNING_ID } }),
    );

    expect(importAlreadyRunningOf(await runRefused())).toBeUndefined();
  });

  it("leaves a plain 409 as an ordinary refusal", async () => {
    serve(() =>
      problemJson({ status: 409, title: "The batch cannot be run.", detail: "The batch has already run." }),
    );

    const failure = await runRefused();

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(409);
    expect((failure as ApiError).code).toBeUndefined();
    expect(importAlreadyRunningOf(failure)).toBeUndefined();
  });
});
