// The `Student` the API seam hands to the screens carries no classification (ADR-009, D-76; QA job
// order AC3: "the Student data model neither requires nor displays classification").
//
// Driven through the public `api.getStudent` with `fetch` stubbed, the way `cardsAndClassificationsApi`
// and `deviceKeyIssued` drive theirs, because `toStudent` is not exported and the seam is the contract.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

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

describe("the Student's organization (ADR-011 D-81)", () => {
  const ID = "00000000-0000-4000-8000-000000000001";

  it("narrows organization: absent, null or string; a non-string fails loud", async () => {
    // A string is read as itself.
    serve(studentJson({ organization: "Computer Society" }));
    expect((await api.getStudent(ID))?.organization).toBe("Computer Society");

    // JSON null — what the endpoint sends for a student with none.
    serve(studentJson({ organization: null }));
    expect((await api.getStudent(ID))?.organization).toBeUndefined();

    // Absent — an older server that predates the field. `studentJson()` carries no such key.
    serve(studentJson());
    expect((await api.getStudent(ID))?.organization).toBeUndefined();

    // Anything else is drift, and is refused by name rather than dropped to `undefined`.
    for (const drifted of [42, true, { name: "Computer Society" }, ["Computer Society"]]) {
      serve(studentJson({ organization: drifted }));
      await expect(api.getStudent(ID)).rejects.toMatchObject({
        name: "ApiError",
        kind: "malformed",
        message: expect.stringContaining("organization"),
      });
    }
  });
});

describe("the Student's department (derived from the programme's college)", () => {
  const ID = "00000000-0000-4000-8000-000000000001";

  it("narrows department: absent, null or string; a non-string fails loud", async () => {
    // A string is read as itself.
    serve(studentJson({ department: "College of Engineering" }));
    expect((await api.getStudent(ID))?.department).toBe("College of Engineering");

    // JSON null — what the endpoint sends for a student with no programme.
    serve(studentJson({ department: null }));
    expect((await api.getStudent(ID))?.department).toBeUndefined();

    // Absent — an older server that predates the field. `studentJson()` carries no such key.
    serve(studentJson());
    expect((await api.getStudent(ID))?.department).toBeUndefined();

    // Anything else is drift, and is refused by name rather than dropped to `undefined`.
    for (const drifted of [42, true, { name: "College of Engineering" }, ["College of Engineering"]]) {
      serve(studentJson({ department: drifted }));
      await expect(api.getStudent(ID)).rejects.toMatchObject({
        name: "ApiError",
        kind: "malformed",
        message: expect.stringContaining("department"),
      });
    }
  });
});

describe("api.exportStudentsCsv", () => {
  const csvOk = () =>
    new Response("Student No.\r\n", {
      status: 200,
      headers: {
        "Content-Type": "text/csv; charset=utf-8",
        "Content-Disposition": "attachment; filename=EAMS-students-2026-10-24.csv",
      },
    });

  it("asks /students/export.csv with the filter and returns the blob and the server's filename", async () => {
    let requested = "";
    vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
      requested = String(input);
      return Promise.resolve(csvOk());
    });

    const file = await api.exportStudentsCsv({ search: "Santos", status: "Active" });

    const url = new URL(requested, "http://localhost");
    expect(url.pathname.endsWith("/students/export.csv")).toBe(true);
    expect(url.searchParams.get("search")).toBe("Santos");
    expect(url.searchParams.get("status")).toBe("Active");
    expect(url.searchParams.has("course")).toBe(false);
    expect(file.filename).toBe("EAMS-students-2026-10-24.csv");
    expect(file.blob.size).toBeGreaterThan(0);
  });

  it("raises ApiError on non-2xx", async () => {
    for (const status of [400, 403, 500]) {
      vi.stubGlobal("fetch", () =>
        Promise.resolve(
          new Response(
            JSON.stringify({ type: "about:blank", title: "Failed.", status, detail: "No.", traceId: "t-1" }),
            { status, headers: { "Content-Type": "application/problem+json" } },
          ),
        ),
      );

      const failure = await api.exportStudentsCsv({}).then(
        () => undefined,
        (cause: unknown) => cause,
      );

      expect(failure).toBeInstanceOf(ApiError);
      expect((failure as ApiError).status).toBe(status);
    }
  });
});
