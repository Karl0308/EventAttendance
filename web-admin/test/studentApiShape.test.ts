// The `Student` the API seam hands to the screens carries no classification (ADR-009, D-76; QA job
// order AC3: "the Student data model neither requires nor displays classification").
//
// Driven through the public `api.getStudent` with `fetch` stubbed, the way `cardsAndClassificationsApi`
// and `deviceKeyIssued` drive theirs, because `toStudent` is not exported and the seam is the contract.

import { afterEach, describe, expect, it, vi } from "vitest";

import { api } from "../src/api";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const studentJson = (extra: Record<string, unknown> = {}) => ({
  id: "00000000-0000-4000-8000-000000000001",
  studentNumber: "2026000001",
  fullName: "Ana Cruz",
  firstName: "Ana",
  middleName: null,
  lastName: "Cruz",
  email: null,
  gender: null,
  photoUrl: null,
  course: "BSCS",
  yearLevel: "1",
  section: "A",
  status: "Active",
  cards: [],
  ...extra,
});

function serve(body: unknown): void {
  vi.stubGlobal("fetch", () => Promise.resolve(json(200, body)));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the Student the API seam returns", () => {
  it("maps a student whose payload has no classifications field", async () => {
    serve(studentJson());

    const student = await api.getStudent("00000000-0000-4000-8000-000000000001");

    expect(student).toBeDefined();
    expect(student?.studentNumber).toBe("2026000001");
    expect(student?.fullName).toBe("Ana Cruz");
    expect(student?.status).toBe("Active");
    expect(student?.cards).toEqual([]);
  });

  it("ignores a classifications field an older server still sends", async () => {
    serve(
      studentJson({
        classifications: [
          {
            classificationId: "v-student",
            name: "STUDENT",
            axis: "Student",
            isActive: true,
            assignedAt: "2026-01-01T00:00:00Z",
          },
        ],
      }),
    );

    const student = await api.getStudent("00000000-0000-4000-8000-000000000001");

    expect(student?.fullName).toBe("Ana Cruz");
    // Not carried onto the mapped object at all — neither as the array nor as an empty default.
    expect(student).not.toHaveProperty("classifications");
  });
});
