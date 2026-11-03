/** @vitest-environment happy-dom */

// #706 F2-3 (W4): the duplicate-card 409 names who holds the card.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
//   1. `ProblemDetails.holder` ({ personType, number, name }) is read onto `ApiError.problem.holder`
//      on the student card route (`CardUidInUse`) and the personnel routes (`DuplicateRfid`).
//   2. The message a screen prints says "This card is already assigned to <name> (<number>)." and not
//      the server's generic detail, whichever population holds the card.
//   3. `holder` is optional: absent (a lost race, a duplicate personnel NUMBER) the server's detail is
//      kept; a malformed `holder` is ignored rather than failing the read of the body it rode in on.
//   4. The personnel form puts the same sentence under the RFID field, and falls back to its existing
//      line when there is no holder.
//   5. The roster-import warning `RfidCardHeldByPersonnel` has its own label (see sisImport.test.ts).

import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

import { ApiError, api, describeApiError } from "../src/api";
import PersonnelFormDialog from "../src/components/PersonnelFormDialog";
import { RFID_TAKEN } from "../src/personnelDraft";
import type { PersonnelWriteRequest } from "../src/types";

const STUDENT_ID = "11111111-1111-4111-8111-111111111111";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", () => Promise.resolve(reply()));
}

async function apiErrorFrom(call: () => Promise<unknown>): Promise<ApiError> {
  try {
    await call();
  } catch (thrown) {
    if (thrown instanceof ApiError) return thrown;
    throw new Error(`Expected an ApiError; got ${String(thrown)}`);
  }
  throw new Error("Expected the call to fail; it succeeded.");
}

const PERSONNEL_REQUEST: PersonnelWriteRequest = {
  personnelNumber: "P-001",
  firstName: "Maria",
  middleName: null,
  lastName: "Santos",
  email: null,
  classification: null,
  department: null,
  organization: null,
  position: null,
  rfidUid: "04A7B8C9",
  status: "Active",
};

const SERVER_DETAIL = "That card UID is already in use.";

const conflict = (code: string, extra: Record<string, unknown> = {}) =>
  json(409, { status: 409, title: "Conflict", detail: SERVER_DETAIL, code, traceId: "t-1", ...extra });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("student card route: CardUidInUse", () => {
  it("names the personnel member who holds the card", async () => {
    serve(() =>
      conflict("CardUidInUse", { holder: { personType: "Personnel", number: "EMP-0042", name: "Pedro Reyes" } }),
    );

    const error = await apiErrorFrom(() => api.addStudentCard(STUDENT_ID, { cardUid: "04A7B8C9", label: null }));

    expect(error.problem?.holder).toEqual({ personType: "Personnel", number: "EMP-0042", name: "Pedro Reyes" });
    expect(describeApiError(error)).toContain("This card is already assigned to Pedro Reyes (EMP-0042).");
    expect(describeApiError(error)).not.toContain(SERVER_DETAIL);
  });

  it("names a student holder the same way", async () => {
    serve(() =>
      conflict("CardUidInUse", { holder: { personType: "Student", number: "2026-0001", name: "Maria Santos" } }),
    );

    const error = await apiErrorFrom(() => api.addStudentCard(STUDENT_ID, { cardUid: "04A7B8C9", label: null }));

    expect(describeApiError(error)).toContain("This card is already assigned to Maria Santos (2026-0001).");
  });

  it("keeps the server's detail when there is no holder (a lost race)", async () => {
    serve(() => conflict("CardUidInUse"));

    const error = await apiErrorFrom(() => api.addStudentCard(STUDENT_ID, { cardUid: "04A7B8C9", label: null }));

    expect(error.problem?.holder).toBeUndefined();
    expect(describeApiError(error)).toContain(SERVER_DETAIL);
    expect(describeApiError(error)).not.toContain("already assigned to");
  });

  it("ignores a malformed holder instead of failing the error body it rode in on", async () => {
    serve(() => conflict("CardUidInUse", { holder: { personType: "Visitor", number: 7 } }));

    const error = await apiErrorFrom(() => api.addStudentCard(STUDENT_ID, { cardUid: "04A7B8C9", label: null }));

    expect(error.problem?.holder).toBeUndefined();
    expect(error.status).toBe(409);
    expect(describeApiError(error)).toContain(SERVER_DETAIL);
  });
});

describe("personnel routes: DuplicateRfid", () => {
  it("reads the holder off POST /personnel", async () => {
    serve(() =>
      conflict("DuplicateRfid", { holder: { personType: "Student", number: "2026-0001", name: "Maria Santos" } }),
    );

    const error = await apiErrorFrom(() => api.createPersonnel(PERSONNEL_REQUEST));

    expect(error.code).toBe("DuplicateRfid");
    expect(error.problem?.holder?.name).toBe("Maria Santos");
  });

  it("the form shows the holder sentence under the RFID field", () => {
    const error = new ApiError("http", 409, "POST /personnel failed (409).", {
      shape: "write",
      problem: {
        status: 409,
        code: "DuplicateRfid",
        holder: { personType: "Student", number: "2026-0001", name: "Maria Santos" },
      },
    });

    render(
      <PersonnelFormDialog
        personnel={undefined}
        onClose={() => {}}
        onSubmit={() => {}}
        running={false}
        failure={{ error }}
        organizations={[]}
      />,
    );

    expect(screen.getByText("This card is already assigned to Maria Santos (2026-0001).")).toBeTruthy();
    expect(screen.queryByText(RFID_TAKEN)).toBeNull();
  });

  it("the form falls back to its existing line when the server named no holder", () => {
    const error = new ApiError("http", 409, "POST /personnel failed (409).", {
      shape: "write",
      problem: { status: 409, code: "DuplicateRfid" },
    });

    render(
      <PersonnelFormDialog
        personnel={undefined}
        onClose={() => {}}
        onSubmit={() => {}}
        running={false}
        failure={{ error }}
        organizations={[]}
      />,
    );

    expect(screen.getByText(RFID_TAKEN)).toBeTruthy();
  });
});
