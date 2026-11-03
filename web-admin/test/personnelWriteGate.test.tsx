/** @vitest-environment happy-dom */

// #706 F2-5: the Personnel page offers add / edit / remove / import only to a token that carries
// `personnel.write`. `personnel.read` alone gets the list, the export, and a note saying why there are
// no write controls.
//
// The gate is a convenience, not a control (the server refuses the same writes with 403), so what is
// pinned here is the UI half: the controls are absent, and the page says why rather than leaving a
// read-only user hunting for them.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";

import AuthProvider from "../src/components/AuthProvider";
import PersonnelPage from "../src/pages/Personnel";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const PERSON = {
  id: "33333333-3333-4333-8333-333333333333",
  personnelNumber: "EMP-0042",
  firstName: "Pedro",
  middleName: null,
  lastName: "Reyes",
  fullName: "Pedro Reyes",
  email: null,
  classification: null,
  department: null,
  organization: null,
  position: null,
  rfidUid: null,
  status: "Active",
};

const userWith = (permissions: string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions,
});

async function showAs(permissions: string[]) {
  resetSessionForTests();
  beginSession("access-token-1", userWith(permissions));
  render(
    <AuthProvider>
      <PersonnelPage />
    </AuthProvider>,
  );
  await act(async () => {});
  await screen.findByText("EMP-0042");
}

beforeEach(() => {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input), "http://localhost").pathname;
    const method = init?.method ?? "GET";
    if (method === "GET" && path === "/api/v1/personnel") {
      return Promise.resolve(json(200, { items: [PERSON], page: 1, pageSize: 200, total: 1, hasMore: false }));
    }
    if (method === "GET" && path === "/api/v1/personnel/organizations") return Promise.resolve(json(200, []));
    throw new Error(`unserved: ${method} ${path}`);
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("Personnel page write gate (#706 F2-5)", () => {
  it("offers add, import, edit and remove to personnel.write", async () => {
    await showAs(["personnel.read", "personnel.write"]);

    expect(screen.getByRole("button", { name: /add personnel/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /import csv/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Edit Pedro Reyes" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Remove Pedro Reyes" })).toBeTruthy();
    expect(screen.queryByText(/personnel\.write permission/i)).toBeNull();
  });

  it("hides every write control from personnel.read alone, keeps the export, and says why", async () => {
    await showAs(["personnel.read"]);

    expect(screen.queryByRole("button", { name: /add personnel/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /import csv/i })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit Pedro Reyes" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove Pedro Reyes" })).toBeNull();
    expect(screen.getByRole("button", { name: /export csv/i })).toBeTruthy();
    expect(screen.getByText(/personnel\.write permission/i)).toBeTruthy();
  });

  it("does not treat students.write as personnel.write (D-104 split)", async () => {
    await showAs(["personnel.read", "students.write"]);

    expect(screen.queryByRole("button", { name: /add personnel/i })).toBeNull();
  });
});
