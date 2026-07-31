// The confirmation that stands between a click and a status change — and the place where what that
// change actually does is said, before it is made rather than after.
//
// It is a separate dialog from the delete confirmation, and not because the code differs. What differs
// is the consequence, and the consequence is the whole content: closing an event writes an `Absent`
// record for every expected student who has none, which is one press and potentially hundreds of rows,
// and it fixes the event's denominator permanently. Cancelling writes the audience down and marks
// nobody. Opening starts recording taps. All four moves in the graph are one-way. A shared "Are you
// sure?" over an interchangeable verb would be the one wording that fits none of them — so the copy is
// per transition and comes from `eventStatus.ts`, where the graph it mirrors lives.
//
// The write itself is the page's, as everywhere else in this slice: a dialog that owns its own write
// has to block its own dismissal for as long as the request runs, and that lock reliably traps a
// keyboard user without closing the hole it was built for.

import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
} from "@mui/material";
import { advise, isResendUnsafe } from "../apiGuidance";
import { isTerminal } from "../eventStatus";
import type { StatusChange } from "../eventStatus";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "change-event-status-dialog-title";
const CONSEQUENCE_ID = "change-event-status-dialog-consequence";
const FINALITY_ID = "change-event-status-dialog-finality";

const HEADING_NOT_CHANGED = "The event's status was not changed";
const HEADING_MAYBE_CHANGED = "The event's status may have been changed";

/**
 * Shown when the confirm button is disabled by the failure rather than by the request in flight — and
 * this endpoint is the one place in the app where that sentence has to do more than apologise.
 *
 * `PATCH /events/{id}/status` is **genuinely idempotent**: the server has an explicit no-op arm, so a
 * request naming the status the event already holds succeeds without re-running the freeze. That arm
 * exists for exactly this situation — a `PATCH` retried after a timeout must not mark a second cohort
 * Absent. `advise()` still answers `may-duplicate` here, because `shape` is a proxy for idempotency
 * and is exact only for `POST`, so the one-click resend is withheld on the app's safest write.
 *
 * The button stays withheld — changing the taxonomy to fix one endpoint is a question owed before D3,
 * not a thing to do here — but the *wording* must not inherit the taxonomy's caution as if it were a
 * fact. What is true is said: this build cannot tell what happened, re-checking and re-attempting is
 * safe, and here is why it is safe.
 *
 * Two sentences, not one, because `retryable !== "safe"` covers two different reasons and only one of
 * them is "we cannot tell". A `malformed` reply is `false`: the API answered in a shape this build
 * cannot read, so a second attempt provably cannot produce a readable answer either. Telling that
 * user to "make the change again" would contradict `advise()`'s own message two lines above it, which
 * says retrying will not help — and it would be the wrong reason as well as the wrong advice.
 *
 * `Retryable`'s doc predicted the first consumer that would need to split `false` from
 * `"may-duplicate"` rather than collapsing both into `!== "safe"`. This is it.
 */
const RESEND_WITHHELD_UNKNOWN =
  "The button is disabled because this build cannot tell whether the change was applied — not " +
  "because attempting it again would be unsafe. It is safe here: the server treats a request naming " +
  "the status an event already holds as a no-op, so a second attempt cannot close the same event " +
  "twice or mark anyone Absent a second time. Close this, reload the page, and look at the status; if " +
  "it has not moved, make the change again.";

const RESEND_WITHHELD_UNREADABLE =
  "The button is disabled because this build cannot read what the API answered, so trying again here " +
  "would not produce an answer it can read either. Close this and reload the page to see the status " +
  "the server actually holds; if it has not moved, this needs whoever maintains EAMS rather than " +
  "another attempt.";

interface ChangeEventStatusDialogProps {
  /** The event's name, so the confirmation names the thing rather than "this event". */
  name: string;
  /** The move being confirmed — its wording, its consequence and its finality. */
  change: StatusChange;
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function ChangeEventStatusDialog({
  name,
  change,
  onClose,
  onConfirm,
  running,
  failure,
}: ChangeEventStatusDialogProps) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    // Described by both paragraphs, not just the first. `aria-describedby` takes a list of ids, and
    // the two say different things — what this does, and that it cannot be taken back. A confirmation
    // announced by its title alone reads as "Close this event? button, button", with the two sentences
    // that would change the answer never spoken.
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={`${CONSEQUENCE_ID} ${FINALITY_ID}`}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>
        {change.verb} “{name}”?
      </DialogTitle>

      <DialogContent>
        <DialogContentText id={CONSEQUENCE_ID}>{change.consequence}</DialogContentText>
        <DialogContentText id={FINALITY_ID} sx={{ mt: 2 }}>
          <strong>This cannot be undone.</strong> {change.finality}
        </DialogContentText>

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_CHANGED}
            mayHaveApplied={HEADING_MAYBE_CHANGED}
            resendWithheld={
              advise(failure.error).retryable === "may-duplicate"
                ? RESEND_WITHHELD_UNKNOWN
                : RESEND_WITHHELD_UNREADABLE
            }
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Dismissal takes focus, not the confirm — the dialog opens with the one-way control a Tab
            away rather than under a keyboard user's finger, so an Enter pressed out of habit (or still
            held from the button that opened this) backs out rather than closing an event.

            "Leave it as it is" rather than "Cancel", which on this dialog is ambiguous in the worst
            possible way: one of the four moves this dialog confirms *is* cancelling the event, and a
            pair of buttons reading "Cancel" and "Cancel this event" is a coin toss. */}
        <Button onClick={onClose} autoFocus>
          Leave it as it is
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          // Red for the two that cannot be come back from; the ordinary accent for opening, which is
          // the routine step in an event's life. Read from the graph rather than from a list of
          // statuses written out here, so a colour cannot disagree with the table.
          color={isTerminal(change.target) ? "error" : "primary"}
          disabled={running || resendUnsafe}
          // "Close this event" rather than "Close": a screen reader moving through the dialog's
          // controls reads the label out of context, and the bare verb does not say what it acts on —
          // nor, for "Cancel", which of the two things it means. The event's name is carried by the
          // title this dialog is labelled by, not repeated here.
        >
          {running ? change.progress : `${change.verb} this event`}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
