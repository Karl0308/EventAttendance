// Subscribes the React tree to the session store, and runs the silent renewal a page load needs.

import { useCallback, useEffect, useMemo, useSyncExternalStore, type ReactNode } from "react";

import { api } from "../api";
import { sessionState, subscribeToSession } from "../authSession";
import { AuthContextProvider, type AuthContextValue } from "../authContext";

/**
 * `useSyncExternalStore` rather than a `useState` kept in step by an effect.
 *
 * The store is written from outside React — `api.ts` calls `beginSession` after a renewal that a
 * request triggered, not a component — so the question this hook exists for is exactly the one being
 * asked: how does a concurrent render read a mutable value it does not own without tearing? An effect
 * mirroring the store into state would render one paint behind it, and the paint it is behind is the
 * one where the session ended.
 *
 * Both snapshot arguments are the same getter. There is no server rendering here; passing it twice is
 * what keeps the signature honest rather than asserting it will never be called.
 */
export default function AuthProvider({ children }: { children: ReactNode }) {
  const session = useSyncExternalStore(subscribeToSession, sessionState, sessionState);

  useEffect(() => {
    // Only from `unknown`, which is where a page load starts and nothing else returns to. Without the
    // guard this would re-run on every session change — including the one it caused — and a sign-out
    // would immediately try to renew the session it had just ended.
    if (session.status !== "unknown") return;

    // The promise is not awaited and its result is not read: `restoreSession` resolves the `unknown`
    // state through the store in every case (see its own note), so there is nothing left here to
    // decide. It cannot reject — `renewSession` returns an outcome rather than throwing.
    //
    // No `live` flag and no cleanup. Under StrictMode this effect runs twice on mount, and the second
    // run joins the first through the single-flight cell rather than making a second call; the store
    // is module state that outlives the component either way, so there is no unmounted `setState`
    // here to guard against.
    void api.restoreSession();
  }, [session.status]);

  const signOut = useCallback(() => api.signOut(), []);

  const value = useMemo<AuthContextValue>(() => ({ session, signOut }), [session, signOut]);

  return <AuthContextProvider value={value}>{children}</AuthContextProvider>;
}
