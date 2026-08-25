// What a failed request means for the user, whether pressing the button again can do anything about
// it, and whether the server may already have acted.
//
// A module of its own rather than a helper beside the component that first needed it. Three screens
// ask this question — `ErrorState`, the event screen's roster alert, and the create-event dialog —
// and a component module cannot export a non-component without tripping `react/only-export-components`,
// so keeping it there left the other callers answering the question by assumption instead. Offering
// Retry on an error that provably cannot clear is a lie the UI tells; offering it on a write that may
// already have been applied is a duplicate the UI causes. One taxonomy, read from one place, is what
// stops both.

import { ApiError } from "./api";

// Statuses this UI actually reacts to. Named so the branch reads as a decision rather than as
// arithmetic, and so nobody has to remember which side of 500 the comparison is on.
const HTTP_UNAUTHORIZED = 401;
const HTTP_FORBIDDEN = 403;
const HTTP_TOO_MANY_REQUESTS = 429;
const HTTP_SERVER_ERROR_FLOOR = 500;

/**
 * Whether pressing the same button again is worth offering, and what it would cost.
 *
 * `"safe"` — the request provably changed nothing, so sending it again is free. True of every read,
 * and of a write the server *decided* against (a 400 refusal, a 409 conflict).
 *
 * `"may-duplicate"` — the request may already have been applied and this client cannot tell. A retry
 * might succeed and might create a second row. `POST /events` carries no idempotency key, so there is
 * nothing to make the second send a no-op. **Do not render a one-click retry for this.**
 *
 * `false` — retrying provably cannot help. Note it does not say the request was harmless: a write that
 * failed `malformed` was answered with a 2xx, so the server accepted it and a second send could still
 * create a second row. Retrying cannot *help*, but it can still *hurt*. **Read `serverEffect`, never
 * this field, before saying what the server did.** Today every consumer collapses `false` and
 * `"may-duplicate"` into `!== "safe"`, so nothing observes the difference — the first consumer to
 * separate "withdraw quietly" from "withdraw and warn about duplication" is the one that would
 * otherwise file write-`malformed` under nothing-to-warn-about.
 *
 * The `false` arm keeps two-valued code compiling, and that is the hazard rather than the
 * convenience: `retryable ? <Retry/> : null` is truthy for `"may-duplicate"` and would put the
 * duplicating button back. **Every consumer tests `retryable === "safe"`.**
 */
export type Retryable = "safe" | "may-duplicate" | false;

/**
 * What the server did before the request failed — which is a different question from whether to offer
 * Retry, and the one a heading has to answer.
 *
 * `"none"` — nothing was changed, and this client knows it: the request never left, or the server
 * answered a decision. `"unknown"` — the request went out and this client cannot say whether it was
 * carried out. A read is always `"none"`: reading changes nothing, so the distinction only ever bites
 * on a write.
 */
export type ServerEffect = "none" | "unknown";

/** What to say, whether Retry is worth offering, and what the server may have done. One decision. */
export interface Guidance {
  message: string;
  retryable: Retryable;
  serverEffect: ServerEffect;
}

/** Said in one place, because it is the instruction that replaces "then retry" on every write. */
const CHECK_FIRST = "Check the list before sending it again.";

/**
 * What the user can do about it, chosen from the machine-readable side of the error only —
 * `ApiError.kind`, `ApiError.status` and `ApiError.shape`. Never from `title`/`detail`: those are
 * prose the server rewords freely, and a UI that pattern-matches them breaks on an edit nobody
 * thought was breaking.
 *
 * `retryable` is decided *inside* this switch on purpose. It used to be a separate predicate beside
 * it, which meant one taxonomy read in two places: a new `ApiErrorKind` failed to compile here, but
 * the predicate would have silently defaulted that kind to retryable. One of the two guards would
 * complain and the other would not, so the quiet one was the one that mattered. `serverEffect` is
 * here for the same reason and not one hop away in the dialog.
 *
 * `shape` is read from the error rather than passed in by the caller. The taxonomy used to be
 * read-shaped throughout — `network` meant "retry, it may work next time", which is true of a GET and
 * is how a connection reset on a POST came to say "the event was not created" and hand the user the
 * Create button. The fact that settles it is known in `send`/`writeJson` and travels on the error; a
 * flag the renderer had to remember to set would be the same defect one level up.
 *
 * The `default` branch is a guard, not a fallback. Assigning `error.kind` to `never` is what makes an
 * unhandled kind a compile error, and unlike leaning on a missing trailing return it does not depend
 * on `strictNullChecks` staying on. Should it ever run anyway, it throws and names the kind: the
 * alternative was returning `undefined` into a caller's destructure, which throws while rendering the
 * error screen — and with no error boundary in `src/`, blanks the page at the exact moment the user
 * was being told what went wrong.
 */
export function advise(error: unknown): Guidance {
  if (!(error instanceof ApiError)) {
    // Not from the seam at all: a synchronous throw on the way to `fetch`, so nothing was sent.
    return {
      message: "Something failed before the request was understood. Retry, and report this if it repeats.",
      retryable: "safe",
      serverEffect: "none",
    };
  }

  // Read once so each arm below reads as "…and on a write, …" rather than re-deriving it five times.
  const write = error.shape === "write";

  switch (error.kind) {
    case "network":
      // Covers the timeout too — `timedOut` raises this kind. A `fetch` rejection cannot tell a
      // connection that never opened from one reset after the request bytes went out, and on a
      // non-idempotent write those two are opposite facts.
      return write
        ? {
            message: `The EAMS API did not answer, so whether it carried the change out is not known. ${CHECK_FIRST}`,
            retryable: "may-duplicate",
            serverEffect: "unknown",
          }
        : {
            message: "The EAMS API did not answer. Check that it is running and reachable, then retry.",
            retryable: "safe",
            serverEffect: "none",
          };

    case "malformed":
      return {
        message: write
          ? `The API answered in a shape this admin build does not recognise, so this build cannot say what it did — the two are probably on different versions. ${CHECK_FIRST} Retrying will not help.`
          : "The API answered in a shape this admin build does not recognise — the two are probably on different versions. Retrying will not help.",
        // `false`, not `"may-duplicate"`, on a write as well. `"may-duplicate"` means "it might work
        // and it might duplicate"; here it provably cannot work, because the two sides disagree on
        // shapes. Withdrawing the button outright is the same answer for the stronger reason, and
        // `serverEffect` still carries the doubt to whatever writes the heading.
        retryable: false,
        serverEffect: write ? "unknown" : "none",
      };

    case "too-large":
      return {
        message:
          "This list has grown past what the admin screen loads in one go. Retrying will not help; it needs server-side paging.",
        retryable: false,
        // A client-side ceiling raised while walking a list. `tooLarge` is unreachable from a write,
        // and a read changes nothing either way.
        serverEffect: "none",
      };

    case "http":
      if (error.status >= HTTP_SERVER_ERROR_FLOOR) {
        // A 500 raised *after* the row committed is the same fact pattern as a connection reset: the
        // status says the server failed, not that it did nothing.
        return write
          ? {
              message: `The server failed while answering, and it may have applied the change before it did. ${CHECK_FIRST} Quote the traceId above if you report it.`,
              retryable: "may-duplicate",
              serverEffect: "unknown",
            }
          : {
              message:
                "The server failed while answering. Retrying often works; if it does not, quote the traceId above.",
              retryable: "safe",
              serverEffect: "none",
            };
      }
      if (error.status === HTTP_UNAUTHORIZED || error.status === HTTP_FORBIDDEN) {
        // ---------------------------------------------------------------------------------------
        // The arm that did not exist before there was a session to lose
        // ---------------------------------------------------------------------------------------
        //
        // These two used to fall into the "every other 4xx" arm below, which answers
        // `retryable: "safe"` and says *"Retry if the reason may have cleared since — you have signed
        // in again, or a conflicting edit has finished."* That sentence was written for a build with
        // no login in it, where "you have signed in again" meant something a user could go and do in
        // another window. It is now wrong in both halves.
        //
        // **A 401 that reaches here has already survived a refresh.** `send` renews the session once
        // and replays the request; a 401 escaping that is a session the server refused to renew, so
        // "retry, it is safe" invites the user to press a button that provably cannot work — and the
        // one thing that *would* work, signing in again, is what the message never says.
        //
        // **A 403 is not a login problem at all**, and telling a user to sign in again is the advice
        // most likely to waste their afternoon. The account is authenticated and lacks the
        // permission; it is the same account after signing in again. So the two share a verdict and
        // not a sentence.
        //
        // `serverEffect: "none"` on a write as well as a read, and that is a real claim rather than a
        // default: authorization runs before the action, so a refused request is one the server
        // decided against rather than one it may have half-applied. `retryable: false` is what stops
        // a form offering the resend anyway.
        return {
          message:
            error.status === HTTP_UNAUTHORIZED
              ? "Your session has ended, so the API refused this and did not apply it. Sign in again, then try once more."
              : "Your account is not permitted to do this, so the API refused it and did not apply it. Ask an EAMS administrator for the permission — signing in again as the same user will not change it.",
          retryable: false,
          serverEffect: "none",
        };
      }
      if (error.status === HTTP_TOO_MANY_REQUESTS) {
        // A 429 usually refuses before anything is handled — but "usually" is not something this
        // client can check. A gateway that rate-limits in front of an origin which already handled
        // the request answers exactly the same way.
        return write
          ? {
              message: `The API is rate-limiting this client, and a refusal at this point does not prove the change was not applied. ${CHECK_FIRST}`,
              retryable: "may-duplicate",
              serverEffect: "unknown",
            }
          : {
              message: "The API is rate-limiting this client. Wait a moment, then retry.",
              retryable: "safe",
              serverEffect: "none",
            };
      }
      // Every other 4xx is the server having *decided*: a validation refusal or a conflict, reached
      // before anything was written. That is the one class of write failure where pressing the button
      // again is as safe as it is on a read.
      //
      // Both sentences used to offer "you have signed in again" as a reason the refusal might have
      // cleared. It is gone from both: 401 and 403 no longer reach this arm, so the only thing left
      // here that clears on its own is somebody else's edit finishing — and an instruction naming an
      // action that cannot apply to any status still routed here is an instruction that sends people
      // to do something irrelevant.
      return {
        message: write
          ? "The API refused the request and did not apply it. Correct what it says above, or send it again if the reason may have cleared since — a conflicting edit has finished, say."
          : "The API refused the request. Retry if the reason may have cleared since — a conflicting edit has finished, say.",
        retryable: "safe",
        serverEffect: "none",
      };

    default: {
      const unhandled: never = error.kind;
      throw new Error(`Unhandled ApiErrorKind: ${String(unhandled)}`);
    }
  }
}

/**
 * Whether a form should refuse to send the same thing again after this failure.
 *
 * `!== "safe"` and never `!retryable`, which is the trap `Retryable`'s own note describes:
 * `"may-duplicate"` is truthy, so a negation goes on offering the button that duplicates the row. It
 * is a named function rather than that expression written out at each submit button because it is the
 * same question every write form asks, and the one form that gets the polarity wrong is the one that
 * creates the second event.
 *
 * Here rather than beside the alert component that also asks it: `react/only-export-components`
 * forbids a component module exporting a non-component, which is why this module exists at all.
 */
export const isResendUnsafe = (error: unknown): boolean => advise(error).retryable !== "safe";
