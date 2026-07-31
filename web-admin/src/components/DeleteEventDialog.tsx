// The confirmation that stands between a click and a soft delete — and, more to the point, the place
// where what a soft delete actually does is said.
//
// Two beliefs an admin can hold here, and both of them are wrong in a way that costs something:
//
//   - "This destroys the attendance records." It does not. `DELETE /events/{id}` sets §4.5's
//     `IsDeleted` and nothing else; every recorded tap survives untouched. An admin who believes
//     otherwise will not press the button they should press, and will go looking for someone with
//     database access to do a thing that did not need doing.
//   - "I can undo this from here." They cannot. The row is restorable — clearing one flag is all it
//     takes — but nothing in this SPA clears it, and there is no endpoint that does either. An admin
//     who believes otherwise presses it to see what happens.
//
// So the dialog says both, rather than asking "Are you sure?" over an event name and leaving the
// consequences to be guessed. The write itself is the page's, as everywhere else in this slice.

import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "delete-event-dialog-title";
const DIALOG_BODY_ID = "delete-event-dialog-body";

const HEADING_NOT_DELETED = "The event was not deleted";
const HEADING_MAYBE_DELETED = "The event may have been deleted";

/**
 * Withheld for the same reason and with the same caveat as the edit form's: `DELETE` is idempotent,
 * so pressing again cannot delete a second anything. `advise()` still withholds the one-click resend,
 * because `shape` is a proxy for idempotency and is exact only for `POST` — it errs safe, and the
 * sentence stays honest about what is actually unknown.
 */
const RESEND_WITHHELD =
  "Delete is disabled because this build cannot tell whether the event was deleted. Close this and " +
  "reload the page — if the event is gone, it was.";

interface DeleteEventDialogProps {
  /** The event's name, so the confirmation names the thing rather than "this event". */
  name: string;
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function DeleteEventDialog({
  name,
  onClose,
  onConfirm,
  running,
  failure,
}: DeleteEventDialogProps) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    // `aria-describedby` as well as `aria-labelledby`: on a confirmation the consequences *are* the
    // content, and a dialog announced by its title alone reads as "Delete this event? button Cancel
    // button Delete" — the two sentences that would change the answer never spoken.
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={DIALOG_BODY_ID}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>Delete “{name}”?</DialogTitle>

      <DialogContent>
        <DialogContentText id={DIALOG_BODY_ID}>
          The event disappears from every list, summary and report in EAMS. The attendance already
          recorded against it is <strong>kept</strong> — nothing is erased. Restoring the event is
          possible, but not from this admin app: it needs someone with database access.
        </DialogContentText>

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_DELETED}
            mayHaveApplied={HEADING_MAYBE_DELETED}
            resendWithheld={RESEND_WITHHELD}
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Cancel takes focus, not Delete. The dialog opens with the destructive control one Tab
            away rather than under a keyboard user's finger, so an Enter pressed out of habit — or
            still held from the button that opened this — dismisses rather than deletes. */}
        <Button onClick={onClose} autoFocus>
          Cancel
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          color="error"
          disabled={running || resendUnsafe}
          // "Delete event" rather than "Delete". A screen reader moving through the dialog's controls
          // reads the label out of context, and the bare verb does not say what it acts on. The event's
          // name is carried by the title this dialog is labelled by, not repeated here.
        >
          {running ? "Deleting…" : "Delete event"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
