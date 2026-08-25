/** @vitest-environment happy-dom */

// `RequireAuth` and `PermissionGuard` — the two components that decide what a signed-in, half-signed-in
// or signed-out visitor is allowed to see.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it deliberately does not render
// ---------------------------------------------------------------------------------------------
//
// The routes below are stand-ins (`<p>Roster</p>`), not the real pages. Mounting `Students` would
// drag in the DataGrid and a screenful of reads that have nothing to do with the question, and the
// question is only ever: which of the three branches did the guard take. The real `App.tsx` pairs
// the same `PermissionGuard` with the same codes; what could drift is a route being added without
// one, and no render test can catch that — it is a review item.
//
// The state case worth naming is **`unknown`**. It is where every page load starts, because the
// access token lives in memory and a reload loses it. A guard that treated it as "anonymous" would
// bounce a perfectly signed-in operator off `/events/{id}` on every F5, and the test for it is the
// one below that asserts a *loading* state rather than a redirect.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { ReactNode } from "react";

import AuthProvider from "../src/components/AuthProvider";
import PermissionGuard, { PermissionDenied } from "../src/components/PermissionGuard";
import RequireAuth, { LOGIN_ROUTE } from "../src/components/RequireAuth";
import { PERMISSIONS } from "../src/permissions";
import { beginSession, endSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const ROSTER_TEXT = "Roster";
const LOGIN_TEXT = "Sign in to EAMS";
const GATED_TEXT = "The device register";

const userWith = (...permissions: string[]): AuthUser => ({
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions,
});

/**
 * The routing shape `App.tsx` uses, cut down to one guarded route and the login screen.
 *
 * `AuthProvider` is real rather than a fake context, because the thing being tested is partly the
 * subscription: the session store is written from outside React (by `api.ts`, after a renewal nobody
 * is watching) and a provider that did not observe it would leave the router a paint behind.
 */
function renderApp(at: string) {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <AuthProvider>
        <Routes>
          <Route path={LOGIN_ROUTE} element={<p>{LOGIN_TEXT}</p>} />
          <Route
            element={
              <RequireAuth>
                <Outlet />
              </RequireAuth>
            }
          >
            <Route path="/students" element={<p>{ROSTER_TEXT}</p>} />
          </Route>
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  resetSessionForTests();
  // `AuthProvider` starts a silent renewal from `unknown`, which reaches `fetch`. Held open, never
  // resolved: the tests that care about the startup state want it *in flight*, and the ones that do
  // not start from a session already in the store, where the effect does not run at all.
  vi.stubGlobal("fetch", () => new Promise<Response>(() => {}));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("RequireAuth", () => {
  it("holds a page load on a loading state rather than bouncing to the login form", () => {
    renderApp("/students");

    // The session is `unknown` and the silent refresh is still running. Redirecting here is the bug
    // where F5 costs a signed-in operator the route they were on.
    // `toHaveTextContent` is a jest-dom matcher and jest-dom is deliberately not a dependency here —
    // the suite asserts on the DOM it renders, not through a matcher library it would then have to
    // keep in step.
    expect(screen.getByRole("status").textContent).toMatch(/still signed in/i);
    expect(screen.queryByText(LOGIN_TEXT)).toBeNull();
    expect(screen.queryByText(ROSTER_TEXT)).toBeNull();
  });

  it("sends an anonymous visitor to the login route", () => {
    resetSessionForTests();
    renderApp("/students");

    // Rendered, then the store says anonymous — which is what a failed refresh does from `api.ts`,
    // with no component involved. The redirect must follow from the store alone.
    screen.getByRole("status");
    endSessionAsExpired();

    expect(screen.getByText(LOGIN_TEXT)).toBeTruthy();
    expect(screen.queryByText(ROSTER_TEXT)).toBeNull();
  });

  it("renders the guarded route for a signed-in user", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.studentsRead));
    renderApp("/students");

    expect(screen.getByText(ROSTER_TEXT)).toBeTruthy();
  });

  it("takes a signed-in user off the guarded route the moment the session ends", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.studentsRead));
    renderApp("/students");
    expect(screen.getByText(ROSTER_TEXT)).toBeTruthy();

    // This is what a failed refresh looks like from the router's side: `api.ts` cleared the session
    // under a request nobody was watching, and the screen has to follow.
    endSessionAsExpired();

    expect(screen.getByText(LOGIN_TEXT)).toBeTruthy();
    expect(screen.queryByText(ROSTER_TEXT)).toBeNull();
  });
});

describe("PermissionGuard", () => {
  const gated = (
    <PermissionGuard
      permission={PERMISSIONS.devicesRead}
      fallback={<PermissionDenied permission={PERMISSIONS.devicesRead} />}
    >
      <p>{GATED_TEXT}</p>
    </PermissionGuard>
  );

  const renderGated = (node: ReactNode) =>
    render(
      <MemoryRouter>
        <AuthProvider>{node}</AuthProvider>
      </MemoryRouter>,
    );

  it("renders the children when the token carries the permission", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.devicesRead, PERMISSIONS.eventsRead));
    renderGated(gated);

    expect(screen.getByText(GATED_TEXT)).toBeTruthy();
  });

  it("renders the fallback when it does not — and names the code, so it can be asked for", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.eventsRead));
    renderGated(gated);

    expect(screen.queryByText(GATED_TEXT)).toBeNull();
    const denial = screen.getByRole("alert");
    expect(denial.textContent).toContain(PERMISSIONS.devicesRead);
  });

  it("renders nothing at all with no fallback, which is what a nav entry wants", () => {
    beginSession("access-token-1", userWith(PERMISSIONS.eventsRead));
    renderGated(
      <PermissionGuard permission={PERMISSIONS.devicesRead}>
        <p>{GATED_TEXT}</p>
      </PermissionGuard>,
    );

    expect(screen.queryByText(GATED_TEXT)).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("does not treat a prefix as a match", () => {
    // `grants` is an exact `includes`, and this is the assertion that keeps it one. A `startsWith`
    // or a `some(p => code.startsWith(p))` written for "namespaces" would make `devices.read` satisfy
    // `devices.readonly` and, far worse, `devices` satisfy everything under it.
    beginSession("access-token-1", userWith("devices"));
    renderGated(gated);

    expect(screen.queryByText(GATED_TEXT)).toBeNull();
  });
});

/**
 * Ends the session the way `api.ts` does when a refresh is refused — **from outside React entirely**,
 * which is the point of routing it through the store rather than through a prop.
 *
 * Wrapped in `act` because the store notification is what schedules the re-render: without it React
 * warns, and the assertion that follows runs against the paint before the redirect.
 */
function endSessionAsExpired(): void {
  act(() => {
    endSession("expired");
  });
}
