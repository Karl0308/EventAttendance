/** @vitest-environment happy-dom */

// The roster's *name*, its *address*, and the *permission that opens it* — pinned together, against
// the real `<App />`.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS FILE EXISTS, AND WHY IT RENDERS `App` RATHER THAN A ROUTE TABLE OF ITS OWN
// ---------------------------------------------------------------------------------------------
//
// The screen behind `/students` was relabelled "Academic Community" in four places — the nav entry,
// the page heading and the two import breadcrumbs — while the route, the permission code and the
// module underneath all deliberately kept saying *students*. That pairing is the whole of the
// change: a later edit that "finishes the rename" by moving the route to `/academic-community`, or
// by swapping the code the route is gated on, is a regression rather than a tidy-up, and before this
// file nothing anywhere said so. All four renamed lines could be reverted and the suite stayed green.
//
// The gate is the half that is easy to pin badly. `authRouting.test.tsx` builds its own `<Route>`
// table, and `studentsPaging.test.tsx` / `studentsClassificationWiring.test.tsx` render `<Students />`
// straight under a `MemoryRouter`. All three are right for what they test and none of them can see
// `App.tsx`: the real path and the real `PermissionGuard` pairing live there and only there, so a
// test that restates them in its own JSX is testing its own fixture. Hence `<App />`, whole, with the
// session store as the only input — which is also how `main.tsx` mounts it.
//
// The token below carries **exactly** `students.read` and nothing else. That is what makes the first
// test pin the code rather than merely the presence of a guard: any other permission put on that
// route refuses this token and the page never paints. The second test comes at it from the other
// side with a token holding every code *except* that one — without it, a route with its guard deleted
// outright would still satisfy the first test.
//
// `fetch` is stubbed to a promise that never settles. Every assertion here is about chrome that is
// rendered outside the read's status branches, so the reads are deliberately left in flight: it keeps
// the file free of roster fixtures it would otherwise have to keep in step with `api.ts`, and there is
// no timer or microtask left for a later test to trip over.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

import App from "../src/App";
import AuthProvider from "../src/components/AuthProvider";
import { PERMISSIONS } from "../src/permissions";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

/** The label all four renamed lines now carry. The URL, the permission and the module do not. */
const ROSTER_LABEL = "Academic Community";

/** Where it still lives, and what `Layout`'s nav entry still points at. */
const ROSTER_PATH = "/students";

const userWith = (...permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: [...permissions],
});

/** Every code this build knows about except the one named — the "is it really gated?" token. */
const everyPermissionExcept = (code: string): readonly string[] =>
  Object.values(PERMISSIONS).filter((p) => p !== code);

/** The app as `main.tsx` mounts it, minus the browser router and the theme. */
function showApp(at: string) {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <AuthProvider>
        <App />
      </AuthProvider>
    </MemoryRouter>,
  );
}

/** The drawer's `<List component="nav" aria-label="Main">`, told apart from the skip link's landmark. */
const mainNav = () => screen.getByRole("navigation", { name: "Main" });

beforeEach(() => {
  resetSessionForTests();
  // Held open, never resolved: see the file note. `AuthProvider` itself never reaches this — the
  // session is already in the store, so its `unknown`-only renewal effect does not run.
  vi.stubGlobal("fetch", () => new Promise<Response>(() => {}));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the roster screen's name, address and permission", () => {
  it("is called Academic Community, still lives at /students, and still opens on students.read", () => {
    // Exactly one code. Swap the route's guard to any other and this token is refused, so the
    // assertions below fail — which is the point of granting nothing else.
    beginSession("access-token-1", userWith(PERMISSIONS.studentsRead));

    showApp(ROSTER_PATH);

    // 1. The nav entry reads the new label — and still points at the old address.
    const entry = within(mainNav()).getByRole("link", { name: ROSTER_LABEL });
    expect(entry.getAttribute("href")).toBe(ROSTER_PATH);

    // 2. `/students` is still the path that matches. If the route were renamed, `<Routes>` would
    //    match nothing here and the page's own heading would never render.
    // 3. …and `students.read` is what let this token through: `PermissionGuard`'s fallback for a
    //    route is `PermissionDenied`, which is an alert, so its absence is the gate having opened.
    expect(screen.getByRole("heading", { name: ROSTER_LABEL })).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("refuses /students to a token that carries every other permission, and names the code it wanted", () => {
    beginSession("access-token-1", userWith(...everyPermissionExcept(PERMISSIONS.studentsRead)));

    showApp(ROSTER_PATH);

    // The route is gated, and gated on this code specifically. A guard deleted from the route, or
    // moved to any code this token happens to hold, renders the page instead of the refusal.
    const denial = screen.getByRole("alert");
    expect(denial.textContent).toContain(PERMISSIONS.studentsRead);

    expect(screen.queryByRole("heading", { name: ROSTER_LABEL })).toBeNull();
    // The nav entry is gated on the same code, so it is not offered either — clicking a link to a
    // refusal is worse than having no link.
    expect(within(mainNav()).queryByRole("link", { name: ROSTER_LABEL })).toBeNull();
  });
});

describe("the import breadcrumbs", () => {
  // Lighter assertions than the pair above: the label and the target, which is all these two lines
  // are. Rendered through `App` all the same, so the crumb's `to` is checked against a route that
  // really exists rather than against a string.

  it("names the roster on /students/import, and leads back to it", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.sisImport));

    showApp("/students/import");

    const crumb = screen.getByRole("link", { name: ROSTER_LABEL });
    expect(crumb.getAttribute("href")).toBe(ROSTER_PATH);
  });

  it("names the roster on /students/import/:batchId, and leads back to it", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.sisImport));

    showApp("/students/import/3f6b1c20-0000-4000-8000-000000000001");

    const crumb = screen.getByRole("link", { name: ROSTER_LABEL });
    expect(crumb.getAttribute("href")).toBe(ROSTER_PATH);
  });
});
