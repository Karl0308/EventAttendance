// Where the access token lives, who it speaks for, and how a screen learns that either changed.
//
// A module of plain state with **no `fetch` in it and no React in it**, and both halves of that are
// deliberate. `api.ts` has to read the token to sign a request and has to write it after a renewal,
// so the store cannot be a hook; the provider has to re-render when a renewal fails under a request
// nobody is watching, so the store cannot be a private variable inside `api.ts`. One mutable cell
// with a subscription is what both of those need, and keeping the transport out of here is what stops
// it becoming a second seam beside `api.ts`.
//
// ---------------------------------------------------------------------------------------------
// WHERE THE TWO CREDENTIALS LIVE, AND WHY NEITHER IS IN STORAGE
// ---------------------------------------------------------------------------------------------
//
// **The refresh token is not here and cannot be.** It is an `httpOnly` cookie (`eams_rt`, scoped to
// the API's `/api/v1/auth` path) that the browser attaches by itself; no script in this bundle can
// read it, and nothing in this file should ever try. That is the whole point of the split — see
// `AuthCookies.cs` — and it is what makes an XSS on this page a fifteen-minute problem instead of a
// permanent account takeover.
//
// **The access token is in a module variable and in nothing else.** Not `localStorage`, not
// `sessionStorage`: those are readable by any script that runs on the origin, and a bearer token
// sitting in one is the exfiltration target the cookie split just removed. The cost is that a page
// reload loses it — and that cost is paid by `POST /auth/refresh` at startup, which is the intended
// path rather than an error. See `AuthProvider`.
//
// The token is deliberately **not** part of the observable state below. A screen never needs it; only
// `api.ts` does. Keeping it out means it is not in a React tree, not in a devtools component
// inspector, and not in anything that serialises props.

import type { AuthUser } from "./types";

/**
 * The readable half of the server's double-submit pair, written by `AuthCookies.Issue` at `Path=/`
 * so a page served from a sibling IIS application can see it.
 */
const CSRF_COOKIE_NAME = "eams_csrf";

/** The header `POST /auth/refresh` and `POST /auth/logout` require that value echoed in. */
export const CSRF_HEADER_NAME = "X-CSRF-Token";

/**
 * Why a session is no longer live — which is not the same question as whether one is, and a login
 * screen that cannot tell them apart either accuses a first-time visitor of having been signed out or
 * says nothing to someone who just was.
 *
 * `expired` — the server refused to renew. The refresh token was expired, unknown, already used, or
 * its CSRF pair did not match; the API deliberately does not say which. `signedOut` — the user asked.
 * `never` — this tab has not held a session since it loaded.
 */
export type SessionEnded = "expired" | "signedOut" | "never";

/**
 * The three states a screen has to render, and `unknown` is the one that is easy to leave out.
 *
 * `unknown` is where every page load starts: the access token was lost with the last render, and
 * whether the browser still holds a renewable session is a question only `POST /auth/refresh` can
 * answer. Collapsing it into `anonymous` would redirect a signed-in user to `/login` on every reload
 * and then bounce them back a moment later — the flash is the visible half, and the lost route is the
 * expensive one.
 */
export type SessionState =
  | { readonly status: "unknown" }
  | { readonly status: "anonymous"; readonly endedBecause: SessionEnded }
  | { readonly status: "signedIn"; readonly user: AuthUser };

const STARTING: SessionState = { status: "unknown" };

// The token, reachable only through the accessor below so that every read is a call `api.ts` makes
// at the moment it composes a header rather than a value something captured earlier and held.
let token: string | undefined;

let state: SessionState = STARTING;

const listeners = new Set<() => void>();

/**
 * The current state, as one stable reference.
 *
 * Identity-stable between changes on purpose: `useSyncExternalStore` re-renders whenever the snapshot
 * is not `Object.is`-equal to the last one, so a getter that built a fresh object per call would
 * render forever.
 */
export const sessionState = (): SessionState => state;

/** The token `api.ts` signs a request with, or `undefined` when there is no live session. */
export const accessToken = (): string | undefined => token;

/** @returns an unsubscribe, as `useSyncExternalStore` expects. */
export function subscribeToSession(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * A sign-in or a renewal landed: the token and the user move together.
 *
 * Together, and not through two setters, because a caller that set one and then the other would put a
 * new token beside the previous user for a render — and the previous user is what `PermissionGuard`
 * reads. One assignment, one notification.
 */
export function beginSession(newToken: string, user: AuthUser): void {
  token = newToken;
  state = { status: "signedIn", user };
  notify();
}

/**
 * The session is over: the token is dropped and every screen is told why.
 *
 * Idempotent by construction — three requests whose renewal all failed call this three times, and the
 * second and third change nothing. It does not call the API; `POST /auth/logout` is `api.signOut`'s
 * job, and this is what runs after it (and what runs *instead* of it when the server has already
 * decided the session is gone).
 */
export function endSession(because: SessionEnded): void {
  token = undefined;
  state = { status: "anonymous", endedBecause: because };
  notify();
}

/**
 * The value of the `eams_csrf` cookie, or `undefined`.
 *
 * Read fresh on every call and never cached: the server re-mints this cookie on **every** login and
 * every refresh (see `AuthCookies.Issue`), so a value read once at startup is wrong from the first
 * renewal onwards, and the failure it produces is a `403 CsrfTokenInvalid` on a request that is
 * otherwise perfectly authenticated.
 *
 * `document` is reached through `globalThis` rather than assumed: this module is imported by `api.ts`,
 * which the suite loads in a plain Node environment for the files that have no DOM.
 */
export function csrfToken(): string | undefined {
  const jar = globalThis.document?.cookie;
  if (jar === undefined || jar === "") return undefined;

  for (const entry of jar.split(";")) {
    const separator = entry.indexOf("=");
    if (separator < 0) continue;
    if (entry.slice(0, separator).trim() !== CSRF_COOKIE_NAME) continue;

    // `decodeURIComponent` because a cookie value is percent-encoded on the way out by anything that
    // writes one; the server's own value is 32 hex characters and survives either way, so this is
    // insurance against a proxy or a future value that does not.
    const value = decodeURIComponent(entry.slice(separator + 1).trim());
    return value === "" ? undefined : value;
  }

  return undefined;
}

/**
 * Resets the module to how it loads. **Test seam, and only that** — nothing in `src/` calls it.
 *
 * It exists because the store is module state and Vitest shares a module registry across the tests in
 * one file: without it, a test that signs in leaks a token into the next test's request, which is the
 * kind of green nobody can explain a week later.
 */
export function resetSessionForTests(): void {
  token = undefined;
  state = STARTING;
  notify();
}

const notify = (): void => {
  for (const listener of listeners) listener();
};
