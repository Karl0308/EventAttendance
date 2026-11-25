// `judgeRememberedImport` — whether the batch this browser remembers still blocks staging a new file.
// The server heartbeats `progressUpdatedAt` at each phase and treats a `Running` batch silent for 30
// minutes as abandoned, so a stamp older than that is the sign the run has stopped. Also covers the 409
// `ImportAlreadyRunning` narrowing and the wording built from the threshold.

import { describe, expect, it } from "vitest";

import { ApiError, IMPORT_ALREADY_RUNNING_CODE } from "./api";
import type { ApiProblem } from "./api";
import {
  IMPORT_APPEARS_STOPPED,
  IMPORT_STALE_AFTER_MINUTES,
  IMPORT_STALE_AFTER_MS,
  importAlreadyRunningOf,
  judgeRememberedImport,
} from "./sisImport";
import { SIS_IMPORT_STATUS } from "./types";

const NOW = Date.parse("2026-11-25T10:00:00.000Z");
const agoIso = (ms: number): string => new Date(NOW - ms).toISOString();
const ONE_MINUTE_MS = 60_000;

describe("judgeRememberedImport", () => {
  it("blocks a Running batch whose progress stamp is fresh", () => {
    const batch = { status: SIS_IMPORT_STATUS.Running, progressUpdatedAt: agoIso(9 * ONE_MINUTE_MS) };
    expect(judgeRememberedImport(batch, NOW)).toBe("running");
  });

  it("still blocks a Running batch exactly at the staleness threshold", () => {
    const batch = { status: SIS_IMPORT_STATUS.Running, progressUpdatedAt: agoIso(IMPORT_STALE_AFTER_MS) };
    expect(judgeRememberedImport(batch, NOW)).toBe("running");
  });

  it("does not block a Running batch whose progress stamp is past the threshold", () => {
    const batch = {
      status: SIS_IMPORT_STATUS.Running,
      progressUpdatedAt: agoIso(IMPORT_STALE_AFTER_MS + ONE_MINUTE_MS),
    };
    expect(judgeRememberedImport(batch, NOW)).toBe("stale");
  });

  it("falls back to startedAt when progressUpdatedAt is absent", () => {
    const old = { status: SIS_IMPORT_STATUS.Running, startedAt: agoIso(IMPORT_STALE_AFTER_MS + ONE_MINUTE_MS) };
    const fresh = { status: SIS_IMPORT_STATUS.Running, startedAt: agoIso(ONE_MINUTE_MS) };
    expect(judgeRememberedImport(old, NOW)).toBe("stale");
    expect(judgeRememberedImport(fresh, NOW)).toBe("running");
  });

  it("prefers progressUpdatedAt over startedAt when both are present", () => {
    const batch = {
      status: SIS_IMPORT_STATUS.Running,
      startedAt: agoIso(IMPORT_STALE_AFTER_MS * 2),
      progressUpdatedAt: agoIso(ONE_MINUTE_MS),
    };
    expect(judgeRememberedImport(batch, NOW)).toBe("running");
  });

  it("keeps blocking a Running batch with no usable timestamp, because staleness cannot be shown", () => {
    expect(judgeRememberedImport({ status: SIS_IMPORT_STATUS.Running }, NOW)).toBe("running");
    expect(
      judgeRememberedImport({ status: SIS_IMPORT_STATUS.Running, progressUpdatedAt: "not a date" }, NOW),
    ).toBe("running");
  });

  it("does not block a batch that is not Running, however old", () => {
    const old = agoIso(IMPORT_STALE_AFTER_MS * 10);
    for (const status of [
      SIS_IMPORT_STATUS.Pending,
      SIS_IMPORT_STATUS.Completed,
      SIS_IMPORT_STATUS.Failed,
    ]) {
      expect(judgeRememberedImport({ status, progressUpdatedAt: old }, NOW)).toBe("not-running");
    }
  });

  it("treats a missing batch as not running", () => {
    expect(judgeRememberedImport(undefined, NOW)).toBe("not-running");
  });
});

describe("the staleness threshold", () => {
  // Pinned to `SisImportRunLiveness.StaleAfter` (30 minutes) on the server. If this fails, the server's
  // value moved or this one did: change them together, never one alone.
  it("is the server's 30 minutes", () => {
    expect(IMPORT_STALE_AFTER_MINUTES).toBe(30);
    expect(IMPORT_STALE_AFTER_MS).toBe(30 * ONE_MINUTE_MS);
  });

  it("is the number the stopped-import wording quotes, and the wording names no other duration", () => {
    expect(IMPORT_APPEARS_STOPPED).toContain(`over ${IMPORT_STALE_AFTER_MS / ONE_MINUTE_MS} minutes`);
    expect(IMPORT_APPEARS_STOPPED).not.toMatch(/half an hour/i);
  });
});

describe("importAlreadyRunningOf", () => {
  const RUNNING_ID = "0b6f5d3e-8c1a-4d52-9f7e-3a1c2b4d5e6f";
  const SERVER_SENTENCE = "Batch 0b6f5d3e (roster.xlsx) is still running. This batch was not changed.";

  const conflict = (problem: Partial<ApiProblem>, status = 409): ApiError =>
    new ApiError("http", status, "The import was started failed (409).", {
      shape: "write",
      problem: { status, ...problem },
    });

  it("returns the server's sentence and the running batch for the coded 409", () => {
    const refusal = importAlreadyRunningOf(
      conflict({ code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: RUNNING_ID, detail: SERVER_SENTENCE }),
    );
    expect(refusal).toEqual({ detail: SERVER_SENTENCE, runningBatchId: RUNNING_ID });
  });

  it("falls back to a fixed sentence when the coded 409 has no detail", () => {
    const refusal = importAlreadyRunningOf(
      conflict({ code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: RUNNING_ID }),
    );
    expect(refusal?.runningBatchId).toBe(RUNNING_ID);
    expect(refusal?.detail).toMatch(/already running/i);
  });

  it("is not fooled by a 409 without the code (term mismatch, batch already ran)", () => {
    expect(importAlreadyRunningOf(conflict({ detail: "The batch has already run." }))).toBeUndefined();
  });

  it("declines a coded 409 that names no batch to link to", () => {
    expect(importAlreadyRunningOf(conflict({ code: IMPORT_ALREADY_RUNNING_CODE }))).toBeUndefined();
    expect(
      importAlreadyRunningOf(conflict({ code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: "" })),
    ).toBeUndefined();
  });

  it("declines the code on any other status", () => {
    expect(
      importAlreadyRunningOf(
        conflict({ code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: RUNNING_ID }, 400),
      ),
    ).toBeUndefined();
  });

  it("declines anything that is not an http ApiError", () => {
    const network = new ApiError("network", 0, "offline", { shape: "write" });
    expect(importAlreadyRunningOf(network)).toBeUndefined();
    expect(importAlreadyRunningOf(new Error("boom"))).toBeUndefined();
    expect(importAlreadyRunningOf({ code: IMPORT_ALREADY_RUNNING_CODE, runningBatchId: RUNNING_ID })).toBeUndefined();
    expect(importAlreadyRunningOf(undefined)).toBeUndefined();
  });
});
