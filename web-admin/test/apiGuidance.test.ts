// `advise()`'s kind × shape matrix, and the polarity trap in how its answer is read.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// Unlike `eventStatus` and `eventDraft`, this module mirrors nothing on the server: it is entirely
// the client's own decision about what to tell a user and whether to offer Retry. So these tests pin
// the whole rule rather than a copy of one, and there is no drift question here.
//
// What they still cannot do is tell you the *classification* is correct — that a 429 really may have
// been applied, for instance. That is a judgement recorded in the module's own comments and taken at
// the D1a gate; the tests hold it steady, they do not re-derive it.
//
// Two separate review gates reproduced this matrix by hand, once by loading the real modules into
// Node behind a load hook. This file is that probe, kept.

import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { ApiError } from "../src/api";
import type { ApiErrorKind, ApiRequestShape } from "../src/api";
import { advise, isResendUnsafe } from "../src/apiGuidance";
import type { Retryable, ServerEffect } from "../src/apiGuidance";

// ---------------------------------------------------------------------------------------------
// The matrix
// ---------------------------------------------------------------------------------------------

const HTTP_BAD_REQUEST = 400;
const HTTP_UNAUTHORIZED = 401;
const HTTP_FORBIDDEN = 403;
const HTTP_CONFLICT = 409;
const HTTP_TOO_MANY_REQUESTS = 429;
const HTTP_SERVER_ERROR = 500;
/** `ApiError.status` when the failure happened before or outside a response. */
const NO_STATUS = 0;

interface Answer {
  readonly retryable: Retryable;
  readonly serverEffect: ServerEffect;
}

/** Nothing was changed and pressing it again is free. Every read, and a write the server decided. */
const SAFE: Answer = { retryable: "safe", serverEffect: "none" };
/** It may already have been applied and this client cannot tell. Never a one-click retry. */
const MAY_DUPLICATE: Answer = { retryable: "may-duplicate", serverEffect: "unknown" };
/** Retrying provably cannot help — which is not the same as the request having been harmless. */
const HOPELESS_AFTER_ACTING: Answer = { retryable: false, serverEffect: "unknown" };
const HOPELESS: Answer = { retryable: false, serverEffect: "none" };

interface Case {
  readonly label: string;
  readonly kind: ApiErrorKind;
  readonly status: number;
  readonly onWrite: Answer;
  readonly onRead: Answer;
}

const MATRIX: readonly Case[] = [
  // The server *decided*, before anything was written. The one class of write failure where
  // pressing the button again is as safe as it is on a read.
  { label: "400 validation refusal", kind: "http", status: HTTP_BAD_REQUEST, onWrite: SAFE, onRead: SAFE },
  { label: "409 conflict", kind: "http", status: HTTP_CONFLICT, onWrite: SAFE, onRead: SAFE },

  // The server decided as well — and decided that this caller may not. Retrying provably cannot
  // help, because nothing about the request changes between the two attempts: a 401 here has already
  // survived one refresh-and-replay inside `send`, and a 403 is the same account either way. They sit
  // in `HOPELESS` rather than `SAFE`, which is where they used to sit and what this change is about.
  {
    label: "401 dead session",
    kind: "http",
    status: HTTP_UNAUTHORIZED,
    onWrite: HOPELESS,
    onRead: HOPELESS,
  },
  {
    label: "403 permission the account lacks",
    kind: "http",
    status: HTTP_FORBIDDEN,
    onWrite: HOPELESS,
    onRead: HOPELESS,
  },

  // The request went out and the answer does not prove the server did nothing.
  {
    label: "429 rate limit",
    kind: "http",
    status: HTTP_TOO_MANY_REQUESTS,
    onWrite: MAY_DUPLICATE,
    onRead: SAFE,
  },
  { label: "500 server error", kind: "http", status: HTTP_SERVER_ERROR, onWrite: MAY_DUPLICATE, onRead: SAFE },
  { label: "network failure", kind: "network", status: NO_STATUS, onWrite: MAY_DUPLICATE, onRead: SAFE },

  // A timeout is not a kind of its own — `api.ts`'s `timedOut()` raises `kind: "network"` so that
  // `advise` reads the two identically. This row records that rather than testing a second path.
  {
    label: "timeout (raised as network by timedOut)",
    kind: "network",
    status: NO_STATUS,
    onWrite: MAY_DUPLICATE,
    onRead: SAFE,
  },

  // The two sides disagree on shapes, so a retry provably cannot work — but on a write the server
  // answered 2xx first, so it did act.
  {
    label: "malformed reply",
    kind: "malformed",
    status: NO_STATUS,
    onWrite: HOPELESS_AFTER_ACTING,
    onRead: HOPELESS,
  },
];

const errorFor = (testCase: Case, shape: ApiRequestShape): ApiError =>
  new ApiError(testCase.kind, testCase.status, `${testCase.label} (${shape})`, { shape });

describe.each(MATRIX)("$label", (testCase: Case) => {
  it("on a write", () => {
    const guidance = advise(errorFor(testCase, "write"));
    expect(guidance.retryable).toBe(testCase.onWrite.retryable);
    expect(guidance.serverEffect).toBe(testCase.onWrite.serverEffect);
  });

  it("on a read", () => {
    const guidance = advise(errorFor(testCase, "read"));
    expect(guidance.retryable).toBe(testCase.onRead.retryable);
    expect(guidance.serverEffect).toBe(testCase.onRead.serverEffect);
  });

  it("says something on both shapes", () => {
    // An empty message would render an alert with a heading and no explanation.
    expect(advise(errorFor(testCase, "write")).message.length).toBeGreaterThan(0);
    expect(advise(errorFor(testCase, "read")).message.length).toBeGreaterThan(0);
  });
});

describe("across the whole matrix", () => {
  it("never reports a read as having changed anything", () => {
    // Reading changes nothing, so the write/read distinction only ever bites on `serverEffect`.
    for (const testCase of MATRIX) {
      expect(advise(errorFor(testCase, "read")).serverEffect).toBe("none");
    }
  });

  it("never offers a plain retry on a write that may already have been applied", () => {
    for (const testCase of MATRIX) {
      const guidance = advise(errorFor(testCase, "write"));
      if (guidance.serverEffect === "unknown") expect(guidance.retryable).not.toBe("safe");
    }
  });

  it("tells the user to check the list on every write whose outcome is unknown", () => {
    for (const testCase of MATRIX) {
      const guidance = advise(errorFor(testCase, "write"));
      if (guidance.serverEffect === "unknown") {
        expect(guidance.message).toContain("Check the list before sending it again.");
      }
    }
  });
});

describe("a session that ended, and a permission an account does not have", () => {
  // ---------------------------------------------------------------------------------------------
  // THE NEGATIVE CONTROL FOR THIS ARM
  // ---------------------------------------------------------------------------------------------
  //
  // Before the 401/403 branch existed, both statuses fell into the "every other 4xx" arm, which
  // answers `retryable: "safe"` and told the user, verbatim: *"Retry if the reason may have cleared
  // since — you have signed in again, or a conflicting edit has finished."*
  //
  // The assertions below are that sentence, inverted. They are the pre-change expectations written as
  // refusals, so if the arm is ever deleted or folded back in, these fail by name rather than the
  // matrix failing with a number. Reverting `advise`'s new branch was run against them and both fail:
  // `expected "safe" to be false`.
  //
  // The reason it is now wrong is not stylistic. `send` renews the session once and replays the
  // request, so **a 401 that reaches `advise` has already survived a refresh** — it is a session the
  // server declined to renew, and inviting the user to press the button again is inviting them to
  // watch it fail identically. The advice they need is the one thing that sentence never said.

  const refusal = (status: number, shape: ApiRequestShape) =>
    new ApiError("http", status, "refused", { shape });

  it("no longer calls a dead session safe to retry", () => {
    for (const shape of ["read", "write"] as const) {
      expect(advise(refusal(HTTP_UNAUTHORIZED, shape)).retryable).not.toBe("safe");
      expect(isResendUnsafe(refusal(HTTP_UNAUTHORIZED, shape))).toBe(true);
    }
  });

  it("no longer offers 'you have signed in again' as a reason a refusal may have cleared", () => {
    // The phrase is gone from the general 4xx arm too: 401 and 403 no longer reach it, so the only
    // thing left there that clears on its own is somebody else's edit finishing.
    for (const status of [HTTP_UNAUTHORIZED, HTTP_FORBIDDEN, HTTP_BAD_REQUEST, HTTP_CONFLICT]) {
      for (const shape of ["read", "write"] as const) {
        expect(advise(refusal(status, shape)).message).not.toContain("signed in again");
      }
    }
  });

  it("sends a dead session to sign in, and says so on a read and a write alike", () => {
    for (const shape of ["read", "write"] as const) {
      expect(advise(refusal(HTTP_UNAUTHORIZED, shape)).message).toContain("Sign in again");
    }
  });

  it("does NOT send a 403 to sign in, because it is the same account either way", () => {
    // The half most easily got wrong. A 403 is authenticated and unauthorised; "sign in again" is the
    // advice most likely to waste the reader's afternoon, and it is what a single shared 401/403
    // message would have told them.
    const guidance = advise(refusal(HTTP_FORBIDDEN, "read"));
    expect(guidance.message).toContain("not permitted");
    expect(guidance.message).toContain("administrator");
    expect(guidance.message).not.toContain("Sign in again");
  });

  it("reports both as having changed nothing, on a write too", () => {
    // A real claim, not a default: authorization runs before the action, so a refused write is one
    // the server decided against rather than one it may have half-applied. It is what keeps a form's
    // heading from saying the outcome is unknown when it is not.
    for (const status of [HTTP_UNAUTHORIZED, HTTP_FORBIDDEN]) {
      expect(advise(refusal(status, "write")).serverEffect).toBe("none");
    }
  });
});

describe("too-large", () => {
  // A client-side ceiling raised while walking a list. `api.ts`'s `tooLarge()` hard-codes
  // `shape: "read"`, so the write case is unreachable and is deliberately not pinned here — an
  // assertion about it would be an assertion about a state the seam cannot produce.
  it("is hopeless and harmless on a read", () => {
    const guidance = advise(new ApiError("too-large", NO_STATUS, "too big", { shape: "read" }));
    expect(guidance.retryable).toBe(false);
    expect(guidance.serverEffect).toBe("none");
  });
});

describe("something that did not come from the seam", () => {
  it("treats a plain Error as nothing having been sent", () => {
    const guidance = advise(new Error("boom"));
    expect(guidance.retryable).toBe("safe");
    expect(guidance.serverEffect).toBe("none");
  });

  it("treats a non-Error throw the same way rather than crashing the error screen", () => {
    const guidance = advise("not an error at all");
    expect(guidance.retryable).toBe("safe");
    expect(guidance.serverEffect).toBe("none");
  });
});

// ---------------------------------------------------------------------------------------------
// The polarity trap
// ---------------------------------------------------------------------------------------------

describe("isResendUnsafe", () => {
  const writeError = (kind: ApiErrorKind, status: number) =>
    new ApiError(kind, status, "m", { shape: "write" });

  it("withholds resend for may-duplicate — which is truthy, and that is the trap", () => {
    const error = writeError("network", NO_STATUS);
    const { retryable } = advise(error);

    expect(retryable).toBe("may-duplicate");
    // `retryable ? <Retry/> : null` would render the button that creates the second event.
    expect(Boolean(retryable)).toBe(true);
    // `!== "safe"` does not.
    expect(isResendUnsafe(error)).toBe(true);
  });

  it("withholds resend for false", () => {
    expect(isResendUnsafe(writeError("malformed", NO_STATUS))).toBe(true);
  });

  it("permits resend only for safe", () => {
    expect(isResendUnsafe(writeError("http", HTTP_BAD_REQUEST))).toBe(false);
    expect(isResendUnsafe(writeError("http", HTTP_CONFLICT))).toBe(false);
  });

  it("agrees with advise on every row of the matrix, both shapes", () => {
    for (const testCase of MATRIX) {
      for (const shape of ["read", "write"] as const) {
        const error = errorFor(testCase, shape);
        expect(isResendUnsafe(error)).toBe(advise(error).retryable !== "safe");
      }
    }
  });
});

// ---------------------------------------------------------------------------------------------
// The consumers, read from source
// ---------------------------------------------------------------------------------------------
//
// `Retryable` is three-valued and one of its values is truthy, so the type cannot stop a consumer
// writing `retryable ? …` — it compiles, and it puts the duplicating button back. The compiler will
// not catch it and no behavioural test of this module can either, because the defect lives at the
// call sites. So this reads the SPA's own source.
//
// Deliberately a *narrow* check, not a linter: it looks for `retryable` used in a boolean position,
// and it requires each mention in a consumer to be an explicit comparison. It does not parse
// TypeScript, and the comment stripping below is conservative rather than exact.

const SRC_DIR = fileURLToPath(new URL("../src", import.meta.url));
/** The module that *defines* the taxonomy assigns these values; only the consumers are constrained. */
const DEFINING_MODULE = "apiGuidance.ts";

interface SourceLine {
  readonly file: string;
  readonly text: string;
}

function sourceFilesIn(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) return sourceFilesIn(full);
    return /\.tsx?$/.test(entry.name) ? [full] : [];
  });
}

/**
 * Code lines only. Block comments go entirely (the modules here carry long JSDoc that discusses
 * `retryable` at length), and whole-line `//` comments go too. Trailing comments are left in place
 * rather than cut at the first `//`, which would truncate any line holding a URL.
 */
function codeLines(file: string): SourceLine[] {
  return readFileSync(file, "utf8")
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .split("\n")
    .filter((text) => !text.trim().startsWith("//"))
    .map((text) => ({ file: path.basename(file), text }));
}

const MENTIONS_RETRYABLE = /\bretryable\b/;
/** `=== "safe"`, `!== "may-duplicate"` — any explicit comparison against a string literal. */
const EXPLICIT_COMPARISON = /[=!]==\s*"/;
/** `const { message, retryable } = advise(error);` — the use is then a later line, also checked. */
const DESTRUCTURING = /^\s*const\s*\{[^}]*\bretryable\b[^}]*\}\s*=/;

const BOOLEAN_POSITIONS: readonly { readonly pattern: RegExp; readonly why: string }[] = [
  // A `!` that is not the `!` of `!==`, somewhere ahead of a `retryable` on the same line. The
  // `[^=\n]*` is what keeps `retryable !== "safe"` out of it: there the `!` is followed by `=`.
  { pattern: /![^=\n]*\bretryable\b/, why: "negated" },
  { pattern: /\bretryable\s*\?/, why: "used as a ternary condition" },
  { pattern: /\bretryable\s*&&/, why: "used as a && operand" },
  { pattern: /\bretryable\s*\|\|/, why: "used as a || operand" },
  { pattern: /\bif\s*\(\s*(?:\w+\.)*retryable\s*\)/, why: "used as an if condition" },
];

describe("how the SPA reads retryable", () => {
  const allLines = sourceFilesIn(SRC_DIR).flatMap(codeLines);
  const mentions = allLines.filter((line) => MENTIONS_RETRYABLE.test(line.text));

  it("actually found the source to check", () => {
    // Without this, a broken path or an over-eager comment stripper makes every assertion below
    // vacuously true — the exact false green this file exists to prevent.
    expect(sourceFilesIn(SRC_DIR).length).toBeGreaterThan(5);
    expect(mentions.length).toBeGreaterThan(0);
  });

  it("never puts retryable in a boolean position", () => {
    const offenders = mentions.flatMap((line) =>
      BOOLEAN_POSITIONS.filter(({ pattern }) => pattern.test(line.text)).map(
        ({ why }) => `${line.file}: ${why} — ${line.text.trim()}`,
      ),
    );

    expect(offenders).toEqual([]);
  });

  it("would catch a truthiness read if one were added", () => {
    // The detector's own negative control. Without it, a regex that matches nothing would report a
    // clean codebase forever — a guard that cannot fail is not a guard.
    const WOULD_REOPEN_THE_DEFECT = [
      "  const offer = retryable ? true : false;",
      "  {guidance.retryable && <RetryButton />}",
      "  if (retryable) return null;",
      "  const hide = !advise(error).retryable;",
      "  const anyRetry = retryable || fallback;",
    ];

    for (const line of WOULD_REOPEN_THE_DEFECT) {
      expect(BOOLEAN_POSITIONS.some(({ pattern }) => pattern.test(line))).toBe(true);
    }
  });

  it("does not flag the comparisons the SPA actually uses", () => {
    const CORRECT_READS = [
      '  const offerRetry = retryable === "safe";',
      '  {guidance.retryable !== "safe" && (',
      '  advise(failure.error).retryable === "may-duplicate"',
      '  export const isResendUnsafe = (error: unknown): boolean => advise(error).retryable !== "safe";',
    ];

    for (const line of CORRECT_READS) {
      expect(BOOLEAN_POSITIONS.some(({ pattern }) => pattern.test(line))).toBe(false);
      expect(EXPLICIT_COMPARISON.test(line)).toBe(true);
    }
  });

  it("reads retryable only through an explicit comparison in every consumer", () => {
    const consumers = mentions.filter((line) => line.file !== DEFINING_MODULE);
    expect(consumers.length).toBeGreaterThan(0);

    const unexplained = consumers
      .filter((line) => !EXPLICIT_COMPARISON.test(line.text) && !DESTRUCTURING.test(line.text))
      .map((line) => `${line.file}: ${line.text.trim()}`);

    expect(unexplained).toEqual([]);
  });
});
