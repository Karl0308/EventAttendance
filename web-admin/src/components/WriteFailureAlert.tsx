// How a failed write is reported, in one place — because the derivation is the part that must not be
// re-done per form.
//
// The heading is the largest, first-read line in the alert, and D1a's review found it asserting one
// thing unconditionally: "The event was not created". For two of the failures a write can produce that
// is false, and it sat two lines above the sentence saying so — the `malformed` raised when a 2xx
// reply will not narrow, whose body reads "The event *was* created…", and a network failure or timeout
// on the way back, whose body reads "may or may not have been carried out". A user who reads the
// heading and stops has been told exactly what those messages exist to prevent them believing.
//
// So the heading is derived from `advise().serverEffect` — from what the seam actually knows about
// what the server did — and the withheld-resend sentence from `advise().retryable`. Both live here so
// that the create form, the edit form and every form after them cannot each get the polarity right
// separately, which is the way one of them eventually gets it wrong.
//
// The two headings and the withheld sentence are props rather than fixed text: what the server did is
// this component's to decide, but what it did it *to* — an event created, a change saved — is the
// caller's, and a heading that said "The write may have been applied" would be a sentence written for
// the seam rather than for the person reading it.

import { Alert, AlertTitle, Typography } from "@mui/material";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";

interface WriteFailureAlertProps {
  error: unknown;
  /** Heading for a failure the server *decided*: a validation refusal, a 409 conflict. */
  notApplied: string;
  /** Heading for a failure whose outcome this build cannot determine. */
  mayHaveApplied: string;
  /**
   * What to do instead, shown only when pressing the button again is not safe. Says why the control
   * is disabled and where the answer actually is — a control that greys out without saying why leaves
   * the user hunting, which is this codebase's standing rule.
   */
  resendWithheld: string;
}

/**
 * `role="alert"` so the refusal is announced rather than merely painted. It is deliberately not given
 * focus, unlike `ErrorState`: the submit button that raised this is still mounted and still under the
 * user's finger, and moving them away from it would cost them their place for no gain.
 */
export function WriteFailureAlert({
  error,
  notApplied,
  mayHaveApplied,
  resendWithheld,
}: WriteFailureAlertProps) {
  const guidance = advise(error);

  return (
    <Alert severity="error" role="alert" sx={{ mb: 2 }}>
      {/* Derived, never asserted. `"unknown"` is the write that went out and may have been applied;
          `"none"` is the server having decided and written nothing. */}
      <AlertTitle>
        {guidance.serverEffect === "unknown" ? mayHaveApplied : notApplied}
      </AlertTitle>
      {/* Carries the server's own sentence. The events write surface names the field and the limit in
          `detail` — and for a locked event, names which scheduling fields it refused to move and says
          to re-send with them left alone — and `httpError` ranks `detail` above both the fixed
          `title` and the machine `code` so that survives to here. */}
      <Typography variant="body2">{describeApiError(error)}</Typography>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {guidance.message}
      </Typography>
      {guidance.retryable !== "safe" && (
        <Typography variant="body2" sx={{ mt: 1 }}>
          {resendWithheld}
        </Typography>
      )}
    </Alert>
  );
}
