// The session, as a screen reads it.
//
// A module with no component in it, for the reason `apiGuidance.ts` records: a component module
// cannot export a non-component without tripping `react/only-export-components`, and the hooks below
// are what every consumer actually imports. The provider that fills this in is
// `components/AuthProvider.tsx`.
//
// It is a thin binding over `authSession.ts` and adds no state of its own. The store is the single
// source of truth precisely because `api.ts` writes to it from outside React — a renewal that fails
// under a request nobody is watching has to reach the router, and it cannot do that through a
// `useState` in a component it has no reference to.

import { createContext, useContext } from "react";

import type { AuthUser } from "./types";
import type { SessionState } from "./authSession";

export interface AuthContextValue {
  readonly session: SessionState;
  /** `POST /auth/logout` and then the local session. Never rejects; see `api.signOut`. */
  readonly signOut: () => Promise<void>;
}

/**
 * `null` rather than a plausible default, and that is the whole reason the hook below exists.
 *
 * A default of `{ status: "anonymous" }` would make a component rendered outside the provider work —
 * by redirecting the user to `/login` forever, with nothing anywhere saying that the provider is
 * missing. Failing at the boundary names the actual mistake.
 */
const AuthContext = createContext<AuthContextValue | null>(null);

export const AuthContextProvider = AuthContext.Provider;

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (value === null) {
    throw new Error("useAuth was called outside <AuthProvider>. Wrap the router in one.");
  }
  return value;
}

/**
 * The signed-in user, for the subtree where there provably is one.
 *
 * Everything below `<RequireAuth>` is in that subtree: it renders its children only when the session
 * is `signedIn`, so by the time `Layout` or a `PermissionGuard` runs, the narrowing has already
 * happened one level up. This hook is how that fact reaches a component *without* a non-null
 * assertion — the throw is unreachable in the tree as it stands, and it is what makes the day someone
 * mounts a gated component outside the guard a named error instead of `undefined.permissions`.
 */
export function useSignedInUser(): AuthUser {
  const { session } = useAuth();
  if (session.status !== "signedIn") {
    throw new Error(
      `A signed-in user was read while the session was "${session.status}". This component belongs ` +
        "below <RequireAuth>.",
    );
  }
  return session.user;
}
