/** @vitest-environment happy-dom */

// `Layout`'s nav entry for the student roster — Task 2 D5. The label now says what the address says
// ("Students" at `/students`); the permission that shows it is unchanged (`students.read`).
//
// Rendered with the real `AuthProvider` and the session store as the only input, the way
// `test/studentsRouteAndNaming.test.tsx` does, so the gate under test is the real `PermissionGuard`.

import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import Layout from "./Layout";
import AuthProvider from "./AuthProvider";
import { PERMISSIONS } from "../permissions";
import { beginSession, resetSessionForTests } from "../authSession";
import type { AuthUser } from "../types";

const userWith = (...permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: [...permissions],
});

function showLayout() {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <AuthProvider>
        <Layout>
          <p>page body</p>
        </Layout>
      </AuthProvider>
    </MemoryRouter>,
  );
}

const mainNav = () => screen.getByRole("navigation", { name: "Main" });

beforeEach(() => {
  resetSessionForTests();
});

afterEach(() => {
  cleanup();
  resetSessionForTests();
});

describe("Layout nav — the student roster entry", () => {
  it("renders the Students nav item linking to /students", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.studentsRead));

    showLayout();

    const entry = within(mainNav()).getByRole("link", { name: "Students" });
    expect(entry.getAttribute("href")).toContain("/students");
  });

  it("drops the old Academic Community label", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.studentsRead));

    showLayout();

    expect(screen.queryByText(/Academic Community/)).toBeNull();
  });

  it("renders the Students entry only under studentsRead", () => {
    // A token holding every code except the one that gates the entry.
    const everyOther = Object.values(PERMISSIONS).filter((p) => p !== PERMISSIONS.studentsRead);
    beginSession("access-token-1", userWith(...everyOther));

    showLayout();

    expect(within(mainNav()).queryByRole("link", { name: "Students" })).toBeNull();
  });
});
