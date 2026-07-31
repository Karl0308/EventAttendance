// What a failed read means for the user, and whether pressing Retry can do anything about it.
//
// A module of its own rather than a helper beside the component that first needed it. Two screens ask
// this question — `ErrorState`, and the event screen's roster alert — and a component module cannot
// export a non-component without tripping `react/only-export-components`, so keeping it there left the
// second caller answering the question by assumption instead. Offering Retry on an error that provably
// cannot clear is a lie the UI tells; one taxonomy, read from one place, is what stops it.

import { ApiError } from "./api";

// Statuses this UI actually reacts to. Named so the branch reads as a decision rather than as
// arithmetic, and so nobody has to remember which side of 500 the comparison is on.
const HTTP_TOO_MANY_REQUESTS = 429;
const HTTP_SERVER_ERROR_FLOOR = 500;

/** What to say, and whether Retry is worth offering. Both answers, one decision. */
export interface Guidance {
  message: string;
  retryable: boolean;
}

/**
 * What the user can do about it, chosen from the machine-readable side of the error only —
 * `ApiError.kind` and `ApiError.status`. Never from `title`/`detail`: those are prose the server
 * rewords freely, and a UI that pattern-matches them breaks on an edit nobody thought was breaking.
 *
 * `retryable` is decided *inside* this switch on purpose. It used to be a separate predicate beside
 * it, which meant one taxonomy read in two places: a new `ApiErrorKind` failed to compile here, but
 * the predicate would have silently defaulted that kind to retryable. One of the two guards would
 * complain and the other would not, so the quiet one was the one that mattered.
 *
 * The `default` branch is that guard, not a fallback. Assigning `error.kind` to `never` is what makes
 * an unhandled kind a compile error, and unlike leaning on the missing trailing return it does not
 * depend on `strictNullChecks` staying on. Should it ever run anyway, it throws and names the kind:
 * the alternative was returning `undefined` into a caller's destructure, which throws while rendering
 * the error screen — and with no error boundary in `src/`, blanks the page at the exact moment the
 * user was being told what went wrong.
 */
export function advise(error: unknown): Guidance {
  if (!(error instanceof ApiError)) {
    return {
      message: "Something failed before the request was understood. Retry, and report this if it repeats.",
      retryable: true,
    };
  }
  switch (error.kind) {
    case "network":
      return {
        message: "The EAMS API did not answer. Check that it is running and reachable, then retry.",
        retryable: true,
      };
    case "malformed":
      return {
        message:
          "The API answered in a shape this admin build does not recognise — the two are probably on different versions. Retrying will not help.",
        retryable: false,
      };
    case "too-large":
      return {
        message:
          "This list has grown past what the admin screen loads in one go. Retrying will not help; it needs server-side paging.",
        retryable: false,
      };
    case "http":
      if (error.status >= HTTP_SERVER_ERROR_FLOOR) {
        return {
          message: "The server failed while answering. Retrying often works; if it does not, quote the traceId above.",
          retryable: true,
        };
      }
      if (error.status === HTTP_TOO_MANY_REQUESTS) {
        return {
          message: "The API is rate-limiting this client. Wait a moment, then retry.",
          retryable: true,
        };
      }
      return {
        message:
          "The API refused the request. Retry if the reason may have cleared since — you have signed in again, or a conflicting edit has finished.",
        retryable: true,
      };
    default: {
      const unhandled: never = error.kind;
      throw new Error(`Unhandled ApiErrorKind: ${String(unhandled)}`);
    }
  }
}
