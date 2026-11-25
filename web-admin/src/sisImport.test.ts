// `judgeRememberedImport` — whether the batch this browser remembers still blocks staging a new file.
// The server stamps `progressUpdatedAt` once, at claim, and never recovers a `Running` batch after an
// app-pool recycle, so a very old stamp on a `Running` batch is the only sign the run has stopped.

import { describe, expect, it } from "vitest";

import { IMPORT_STALE_AFTER_MS, judgeRememberedImport } from "./sisImport";
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
