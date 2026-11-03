/** @vitest-environment happy-dom */

// #706 F2-6: an unresolved-scans row whose card resolved to a personnel member but was tapped on a
// capture app that cannot record personnel (`reason = "PersonnelClientNotCapable"`) says so, in words.
// A scan with no reason prints exactly what it printed before.

import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

import UnresolvedScansPanel from "../src/components/UnresolvedScansPanel";
import { describeScanReason } from "../src/scanReasons";
import type { EventScan, EventScanLog } from "../src/types";

afterEach(cleanup);

const scan = (patch: Partial<EventScan>): EventScan => ({
  cardUid: "04A7B8C9",
  scannedAt: "2026-09-01T01:05:00Z",
  recordedAt: "2026-09-01T01:05:01Z",
  deviceTapId: `tap-${patch.cardUid ?? "a"}`,
  serverOutcome: "CardNotFound",
  ...patch,
});

const logOf = (scans: EventScan[]): EventScanLog => ({
  eventId: "e1",
  totalScans: scans.length,
  distinctCards: new Set(scans.map((s) => s.cardUid)).size,
  scans,
});

const show = (scans: EventScan[]) =>
  render(<UnresolvedScansPanel read={{ status: "ready", log: logOf(scans) }} onRetry={() => {}} />);

describe("Unrecognised scans: the personnel-on-an-old-app reason (#706 F2-6)", () => {
  it("says 'Personnel card, tapped on an outdated app' for PersonnelClientNotCapable", () => {
    show([scan({ personnelId: "p1", reason: "PersonnelClientNotCapable" })]);

    expect(screen.getByText("Personnel card, tapped on an outdated app")).toBeTruthy();
  });

  it("adds nothing to a scan that has no reason", () => {
    show([scan({ cardUid: "04FFEEDD" })]);

    expect(screen.queryByText(/outdated app/i)).toBeNull();
    expect(screen.getByText("CardNotFound")).toBeTruthy();
  });

  it("shows a reason this build has never heard of verbatim, rather than hiding it", () => {
    expect(describeScanReason("SomeFutureReason")).toBe("SomeFutureReason");
    expect(describeScanReason(undefined)).toBeUndefined();
  });
});
