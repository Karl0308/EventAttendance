/** @vitest-environment happy-dom */

// The session half of `src/api.ts`: what signs a request, what happens when the server says the
// signature is no longer good, and what must NOT happen when three requests say it at once.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and why the concurrency case is the one that matters
// ---------------------------------------------------------------------------------------------
//
// Refresh-token rotation on this API is **single-use, and a second presentation of an
// already-rotated token is read as a replay** — which revokes every live token in the family
// (`AuthController.Refresh` says so in its own remarks). So the failure mode of a naive
// refresh-on-401 is not "some redundant network calls". It is: a dashboard that fires four reads in
// one `Promise.all` meets an expired access token, sends four refreshes with the same cookie, and
// the user is signed out of every tab by their own home page loading. Nothing about that reproduces
// by hand — it needs concurrency and an expired token at the same moment — which is exactly why it
// is pinned here rather than left to a manual check.
//
// The other half of the same guard is the sequential case: a request that was answered *slowly* and
// comes back 401 after somebody else already renewed must not spend a rotation of its own. Both are
// below, and they fail for different reasons if either half is removed.
//
// It runs in `happy-dom` because the subject reaches `document.cookie` (the CSRF pair) and
// `sessionStorage` (the device key, in the last test).

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import path from "node:path";

import { ApiError, api } from "../src/api";
import { advise } from "../src/apiGuidance";
import { accessToken, resetSessionForTests, sessionState } from "../src/authSession";
import { setDeviceKey } from "../src/deviceKey";

// ---------------------------------------------------------------------------------------------
// A server the test writes the answers for
// ---------------------------------------------------------------------------------------------

interface Recorded {
  readonly url: string;
  readonly method: string;
  readonly headers: Readonly<Record<string, string>>;
  readonly credentials: string | undefined;
  readonly body: string | undefined;
}

type Handler = (request: Recorded) => Promise<Response>;

const calls: Recorded[] = [];

const headersOf = (init: RequestInit | undefined): Record<string, string> => {
  const headers = init?.headers;
  if (headers === undefined) return {};
  if (headers instanceof Headers) return Object.fromEntries(headers.entries());
  if (Array.isArray(headers)) return Object.fromEntries(headers);
  return { ...headers };
};

/**
 * Installs the stub. Every request is recorded before the handler sees it, so an assertion about
 * what went on the wire never depends on the handler having been written to capture it.
 */
function serve(handler: Handler): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const body = typeof init?.body === "string" ? init.body : undefined;
    const recorded: Recorded = {
      url: String(input),
      method: init?.method ?? "GET",
      headers: headersOf(init),
      credentials: init?.credentials,
      body,
    };
    calls.push(recorded);
    return handler(recorded);
  });
}

const json = (status: number, body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

/** RFC 7807, as this API emits it — `code` and `traceId` on every refusal. */
const problem = (status: number, code: string): Response =>
  json(status, {
    status,
    title: "The request was refused.",
    detail: `A refusal the test wrote, coded ${code}.`,
    traceId: "00-testtrace-0000000000000000-01",
    code,
  });

const USER = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read", "events.read"],
};

/** `AuthTokenResponse`. `expiresAt` is present and deliberately not read — see `issuedSessionFrom`. */
const issued = (token: string) => ({
  accessToken: token,
  tokenType: "Bearer",
  expiresAt: "2026-08-25T09:15:00Z",
  user: USER,
});

/** One page of `<Dto>PagedResult`, sized for `countStudents` — which maps no items at all. */
const page = (total: number) => ({ items: [], page: 1, pageSize: 1, total, hasMore: false });

/** A promise the test decides when to settle — the same idiom `useApiMutation.test.ts` uses. */
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let captured: ((value: T) => void) | undefined;
  const promise = new Promise<T>((resolve) => {
    captured = resolve;
  });
  if (captured === undefined) throw new Error("The Promise executor did not run synchronously.");
  return { promise, resolve: captured };
}

const FIRST_TOKEN = "access-token-1";
const SECOND_TOKEN = "access-token-2";
const CSRF_VALUE = "0123456789abcdef0123456789abcdef";

const isRefresh = (request: Recorded) => request.url.endsWith("/auth/refresh");
const isStudents = (request: Recorded) => request.url.includes("/students");
const bearerOf = (request: Recorded) => request.headers.Authorization;

const refreshCalls = () => calls.filter(isRefresh).length;
const studentCalls = () => calls.filter(isStudents).length;

/** Signs in, so the tests below start from a live session with `FIRST_TOKEN` in it. */
async function signInWithFirstToken(): Promise<void> {
  serve((request) =>
    Promise.resolve(
      request.url.endsWith("/auth/login")
        ? json(200, issued(FIRST_TOKEN))
        : problem(500, "TheTestDidNotExpectThis"),
    ),
  );
  await api.signIn(USER.email, "correct horse");
  calls.length = 0;
}

beforeEach(() => {
  calls.length = 0;
  resetSessionForTests();
  // The readable half of the double-submit pair, as `AuthCookies.Issue` writes it: `Path=/`, no
  // `httpOnly`. The refresh token has no counterpart here and cannot have one — it is `httpOnly`, so
  // no test and no bug can read it either.
  document.cookie = `eams_csrf=${CSRF_VALUE}; path=/`;
});

afterEach(() => {
  vi.unstubAllGlobals();
  document.cookie = "eams_csrf=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT";
});

// ---------------------------------------------------------------------------------------------
// Signing in
// ---------------------------------------------------------------------------------------------

describe("signIn", () => {
  it("puts the session in the store and signs the next request with it", async () => {
    await signInWithFirstToken();

    expect(sessionState()).toEqual({ status: "signedIn", user: USER });

    serve(() => Promise.resolve(json(200, page(61))));
    await api.countStudents();

    expect(bearerOf(calls[0])).toBe(`Bearer ${FIRST_TOKEN}`);
  });

  it("sends the cookie-bearing routes with credentials and the ordinary ones without", async () => {
    await signInWithFirstToken();
    serve(() => Promise.resolve(json(200, page(0))));
    await api.countStudents();

    // `include` on `/auth/*` is what lets the browser store and return `eams_rt`. `same-origin`
    // everywhere else is the deliberate opposite: those routes authenticate with a header, so there
    // is nothing to attach and no reason to widen what a same-site page could ride on.
    expect(calls[0].credentials).toBe("same-origin");
  });

  it("raises the server's own sentence and its traceId, and stays anonymous", async () => {
    serve(() => Promise.resolve(problem(401, "InvalidCredentials")));

    await expect(api.signIn(USER.email, "wrong")).rejects.toThrow(ApiError);
    expect(sessionState().status).toBe("unknown");
    expect(accessToken()).toBeUndefined();

    // Caught a second time to read the fields — `rejects.toThrow` cannot.
    const error = await api.signIn(USER.email, "wrong").catch((cause: unknown) => cause);
    expect(error).toBeInstanceOf(ApiError);
    if (!(error instanceof ApiError)) throw new Error("unreachable — narrowed above");
    expect(error.status).toBe(401);
    expect(error.code).toBe("InvalidCredentials");
    expect(error.traceId).toBe("00-testtrace-0000000000000000-01");
    expect(error.message).toContain("A refusal the test wrote");
  });

  it("refuses a reply whose tokenType is not Bearer rather than composing a header from it", async () => {
    serve(() => Promise.resolve(json(200, { ...issued(FIRST_TOKEN), tokenType: "MAC" })));

    await expect(api.signIn(USER.email, "correct horse")).rejects.toThrow(/tokenType/);
    expect(accessToken()).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------------------------
// The one that matters
// ---------------------------------------------------------------------------------------------

describe("a 401 under concurrency", () => {
  it("shares ONE refresh between three simultaneous 401s, and replays each request once", async () => {
    await signInWithFirstToken();

    // Held open so all three reads have provably met their 401 before any renewal can finish. Without
    // the gate the test would still usually pass, and "usually" is not what this file is for.
    const gate = deferred<void>();

    serve(async (request) => {
      if (isRefresh(request)) {
        await gate.promise;
        return json(200, issued(SECOND_TOKEN));
      }
      return bearerOf(request) === `Bearer ${FIRST_TOKEN}`
        ? problem(401, "TokenExpired")
        : json(200, page(7));
    });

    const reads = Promise.all([api.countStudents(), api.countStudents(), api.countStudents()]);

    // Let the three 401s land and the renewal be requested, then release it.
    await Promise.resolve();
    await Promise.resolve();
    gate.resolve();

    expect(await reads).toEqual([7, 7, 7]);

    // THE assertion. Three would burn the token family and sign the user out of every tab.
    expect(refreshCalls()).toBe(1);

    // Three original attempts, three replays: one replay each, and no third attempt for any of them.
    expect(studentCalls()).toBe(6);
    expect(calls.filter((c) => isStudents(c) && bearerOf(c) === `Bearer ${FIRST_TOKEN}`)).toHaveLength(3);
    expect(calls.filter((c) => isStudents(c) && bearerOf(c) === `Bearer ${SECOND_TOKEN}`)).toHaveLength(3);
  });

  it("does not spend a second rotation on a 401 answered after someone else renewed", async () => {
    await signInWithFirstToken();

    // The slow read's first attempt, held until the fast one has finished renewing. This is the
    // sequential half of the guard: the in-flight cell is empty by then, so only the comparison
    // between the token this attempt used and the token the store now holds can stop it.
    const slow = deferred<void>();
    let studentAttempts = 0;

    serve(async (request) => {
      if (isRefresh(request)) return json(200, issued(SECOND_TOKEN));

      studentAttempts += 1;
      if (studentAttempts === 1) await slow.promise;

      return bearerOf(request) === `Bearer ${FIRST_TOKEN}`
        ? problem(401, "TokenExpired")
        : json(200, page(3));
    });

    const slowRead = api.countStudents();
    const fastRead = api.countStudents();

    expect(await fastRead).toBe(3);
    expect(refreshCalls()).toBe(1);

    slow.resolve();
    expect(await slowRead).toBe(3);

    expect(refreshCalls()).toBe(1);
  });
});

describe("a 401 that the refresh cannot fix", () => {
  it("ends the session, clears the token, and surfaces the original refusal", async () => {
    await signInWithFirstToken();

    serve((request) =>
      Promise.resolve(
        isRefresh(request) ? problem(401, "SessionExpired") : problem(401, "TokenExpired"),
      ),
    );

    const error = await api.countStudents().catch((cause: unknown) => cause);

    expect(sessionState()).toEqual({ status: "anonymous", endedBecause: "expired" });
    expect(accessToken()).toBeUndefined();

    // One renewal, one refused attempt, no replay — there was nothing to replay it with.
    expect(refreshCalls()).toBe(1);
    expect(studentCalls()).toBe(1);

    // And it must not reach the user as something worth pressing again. This is the other half of
    // `advise()`'s new arm: before it, this exact error answered `retryable: "safe"` and told them to
    // retry because "you have signed in again".
    expect(advise(error).retryable).toBe(false);
    expect(advise(error).message).toContain("Sign in again");
  });

  it("ends the session on a 403 too, because a lost CSRF cookie is a session that cannot renew", async () => {
    await signInWithFirstToken();
    // What a browser restart leaves behind: `eams_rt` persists (it has an `Expires`), the
    // session-scoped `eams_csrf` does not. The refresh then arrives with no header and is answered
    // 403 — which is a session this browser cannot renew, not a malformed request the user can fix.
    document.cookie = "eams_csrf=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT";

    serve((request) =>
      Promise.resolve(
        isRefresh(request) ? problem(403, "CsrfTokenInvalid") : problem(401, "TokenExpired"),
      ),
    );

    await api.countStudents().catch(() => undefined);

    expect(calls.filter(isRefresh)[0].headers["X-CSRF-Token"]).toBeUndefined();
    expect(sessionState()).toEqual({ status: "anonymous", endedBecause: "expired" });
  });

  it("leaves a live session alone when the renewal could not reach the API", async () => {
    await signInWithFirstToken();

    serve((request) => {
      if (isRefresh(request)) return Promise.reject(new TypeError("Failed to fetch"));
      return Promise.resolve(problem(401, "TokenExpired"));
    });

    await api.countStudents().catch(() => undefined);

    // A dropped connection is not evidence that a session ended, and signing the user out over one
    // throws away whatever they had typed — while offering them a login form they cannot submit
    // either, because the API is exactly as unreachable for that.
    expect(sessionState().status).toBe("signedIn");
  });

  it("does not renew a 401 on a request that carried no token", async () => {
    serve(() => Promise.resolve(problem(401, "Unauthorized")));

    await api.countStudents().catch(() => undefined);

    // Nothing signed it, so nothing could have expired. Spending a rotation here would be a refresh
    // triggered by a screen that was never signed in.
    expect(refreshCalls()).toBe(0);
  });
});

// ---------------------------------------------------------------------------------------------
// The startup path, and the way out
// ---------------------------------------------------------------------------------------------

describe("restoreSession", () => {
  it("recovers a session a page reload lost, which is the intended startup path", async () => {
    serve((request) =>
      Promise.resolve(isRefresh(request) ? json(200, issued(SECOND_TOKEN)) : problem(500, "No")),
    );

    await api.restoreSession();

    expect(sessionState()).toEqual({ status: "signedIn", user: USER });
    expect(calls[0].headers["X-CSRF-Token"]).toBe(CSRF_VALUE);
    expect(calls[0].credentials).toBe("include");
  });

  it("resolves the unknown state even when the API cannot be reached", async () => {
    serve(() => Promise.reject(new TypeError("Failed to fetch")));

    await api.restoreSession();

    // `never`, not `expired`: "we could not find out" must not greet a first-time visitor as "you
    // were signed out". What matters most is that it is no longer `unknown` — that would hold the
    // app on its loading screen for as long as the API stayed down.
    expect(sessionState()).toEqual({ status: "anonymous", endedBecause: "never" });
  });
});

describe("signOut", () => {
  it("calls logout with the CSRF header and a Bearer token, then clears the session", async () => {
    await signInWithFirstToken();
    serve(() => Promise.resolve(new Response(null, { status: 204 })));

    await api.signOut();

    expect(calls[0].url).toContain("/auth/logout");
    expect(calls[0].headers["X-CSRF-Token"]).toBe(CSRF_VALUE);
    expect(calls[0].headers.Authorization).toBe(`Bearer ${FIRST_TOKEN}`);
    expect(calls[0].credentials).toBe("include");
    expect(sessionState()).toEqual({ status: "anonymous", endedBecause: "signedOut" });
  });

  it("still ends the local session when the server refuses", async () => {
    await signInWithFirstToken();
    serve(() => Promise.resolve(problem(403, "CsrfTokenInvalid")));

    await api.signOut();

    // Refusing to clear would leave someone who pressed Sign out looking at a signed-in interface.
    // The console line beside this in `api.signOut` is what keeps the server's refusal from vanishing.
    expect(sessionState()).toEqual({ status: "anonymous", endedBecause: "signedOut" });
  });
});

// ---------------------------------------------------------------------------------------------
// The device-key tap path, which none of the above may touch
// ---------------------------------------------------------------------------------------------
//
// Two credentials reach this API and they never mix. A kiosk sends `Authorization: DeviceKey …` and
// is scoped to `attendance.capture`; a person sends `Bearer`. A 401 on the tap path is a **device
// key** — malformed, unknown, or from another server — and it is fixed by pasting a new one on the
// page, not by renewing anybody's session. Renewing there would spend a rotation on a failure a
// rotation cannot fix, and would file a device problem under the user's account.

const DEVICE_KEY = `eams_dk_096b2085a1c3_${"a".repeat(64)}`;
const EVENT_ID = "33333333-3333-3333-3333-333333333333";
const CARD_UID = "0123456789";

describe("the tap simulator", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    expect(setDeviceKey(DEVICE_KEY).ok).toBe(true);
  });

  it("signs with the device key, and a 401 there renews nothing", async () => {
    await signInWithFirstToken();
    serve(() => Promise.resolve(problem(401, "DeviceKeyUnknown")));

    const result = await api.tap(EVENT_ID, CARD_UID);

    expect(result.ok).toBe(false);
    expect(result.message).toContain("the device key is malformed");

    const tapCall = calls.filter((c) => c.url.includes("/attendance/tap"))[0];
    expect(tapCall.headers.Authorization).toBe(`DeviceKey ${DEVICE_KEY}`);
    expect(tapCall.headers.Authorization).not.toContain("Bearer");

    // The two facts that would break if the tap were ever routed through `send`.
    expect(refreshCalls()).toBe(0);
    expect(sessionState().status).toBe("signedIn");
  });
});

// ---------------------------------------------------------------------------------------------
// The two constants the session work must not have moved, read from source
// ---------------------------------------------------------------------------------------------
//
// `RETRYABLE_CLIENT_STATUSES` and `tapIsAnsweredForGood` are module-private, so no behavioural test
// can name them — and they are the pair most likely to look like tidying to someone who has just
// finished teaching the SPA that a 401 means "renew the session". They mean nothing of the sort
// there: 401 and 403 are *device credential states* a paste can change, which is why they are the
// statuses whose `deviceTapId` is kept for the retry. Folding them into the session's reading of 401
// re-opens the `TappedAtOutOfRange` bug class their own docblock describes — every retry resending
// the very timestamp that was just refused, with no escape but reloading the tab.

// Resolved from the working directory rather than from `import.meta.url`, which is what
// `apiGuidance.test.ts` uses: under `happy-dom` that is an `http://localhost/…` URL and
// `fileURLToPath` refuses it. Vitest runs with the package root as its cwd.
// Line endings normalised, because `core.autocrlf` decides them and CI's checkout and a Windows
// working copy disagree. A two-line assertion that passes on the runner and fails on the author's
// machine is worse than no assertion.
const API_SOURCE = readFileSync(path.join(process.cwd(), "src", "api.ts"), "utf8").replace(
  /\r\n/g,
  "\n",
);

describe("the tap path's retry taxonomy", () => {
  it("actually found the source to check", () => {
    // Without this, a wrong path would make both assertions below throw rather than pass — but a
    // *truncated* read would make them fail for a reason that names nothing. Anchoring on a line
    // nobody would delete says the file is the one meant.
    expect(API_SOURCE).toContain("const TAP_WHAT = \"The simulated tap\";");
  });

  it("still keeps the attempt alive for exactly 401, 403, 408 and 429", () => {
    expect(API_SOURCE).toContain("const RETRYABLE_CLIENT_STATUSES = new Set([401, 403, 408, 429]);");
  });

  it("still calls every other 4xx answered for good", () => {
    expect(API_SOURCE).toContain(
      "const tapIsAnsweredForGood = (status: number) =>\n" +
        "  status >= 400 && status < 500 && !RETRYABLE_CLIENT_STATUSES.has(status);",
    );
  });
});
