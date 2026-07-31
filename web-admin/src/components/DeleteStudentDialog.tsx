// The confirmation that stands between a click and a soft delete — and, more to the point, the place
// where what a soft delete actually does is said.
//
// Four beliefs an admin can hold here. Two of them are the ones `DeleteEventDialog` records, and two
// are specific to a student and are the reason this is not that dialog with a different noun:
//
//   - "This destroys their attendance." It does not. `DELETE /students/{id}` sets §4.3's `IsDeleted`
//     and nothing else; every recorded tap survives.
//   - "I can undo this from here." They cannot, and §6.2 defines no endpoint that restores a student
//     at all — this is less recoverable than a deleted event, not more.
//   - "Their card is now free to give to someone else." It is not, and this is the one that costs a
//     working day. The cards are deliberately left ACTIVE — deactivating them here would rewrite the
//     issuance history ADR-001 D-3 exists to preserve, on an operation nobody asked for — so the UID
//     keeps its slot in the active-card unique index. Handing that physical card to another student
//     answers 409 until it is detached, and an admin who did not expect that reads it as a bug.
//   - "Their student number is free again." It is not. The uniqueness index is not filtered on
//     `IsDeleted`, so re-creating them under the same number is refused too.

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

const DIALOG_TITLE_ID = "delete-student-dialog-title";
const DIALOG_BODY_ID = "delete-student-dialog-body";

const HEADING_NOT_DELETED = "The student was not deleted";
const HEADING_MAYBE_DELETED = "The student may have been deleted";

/**
 * Withheld for the same reason and with the same caveat as the event delete's: `DELETE` is idempotent,
 * so pressing again cannot delete a second anything. `advise()` still withholds the one-click resend,
 * because `shape` is a proxy for idempotency and is exact only for `POST` — it errs safe, and the
 * sentence stays honest about what is actually unknown.
 */
const RESEND_WITHHELD =
  "Delete is disabled because this build cannot tell whether the student was deleted. Close this and " +
  "search the list — if they are gone, they were.";

/** Said only when there is a card it could be true of; otherwise it is a warning about nothing. */
const cardsStayActive = (activeCards: number) =>
  `${activeCards === 1 ? "Their RFID card stays active" : `Their ${activeCards} RFID cards stay active`} ` +
  "on purpose, so the issuance history and every past tap keep resolving. They cannot record " +
  `attendance any more — a deleted student is unreachable from the tap path — but the ` +
  `${activeCards === 1 ? "UID is" : "UIDs are"} still taken: handing that physical card to another ` +
  "student is refused until it is detached here first. Detach it before deleting if it is being " +
  "re-issued.";

interface DeleteStudentDialogProps {
  /** The student's name, so the confirmation names the person rather than "this student". */
  name: string;
  /** How many of their cards are still active. Decides whether the card sentence is said at all. */
  activeCards: number;
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function DeleteStudentDialog({
  name,
  activeCards,
  onClose,
  onConfirm,
  running,
  failure,
}: DeleteStudentDialogProps) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    // `aria-describedby` as well as `aria-labelledby`: on a confirmation the consequences *are* the
    // content, and a dialog announced by its title alone reads as "Delete this student? button Cancel
    // button Delete" — with the sentences that would change the answer never spoken.
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
          The student disappears from every list, roster and report in EAMS. The attendance already
          recorded for them is <strong>kept</strong> — nothing is erased. Their student number stays
          taken, so the same number cannot be re-used, and restoring the student is{" "}
          <strong>not possible from this admin app</strong>: it needs someone with database access.
        </DialogContentText>

        {activeCards > 0 && (
          <DialogContentText sx={{ mt: 2 }}>{cardsStayActive(activeCards)}</DialogContentText>
        )}

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
        {/* Cancel takes focus, not Delete. The dialog opens with the destructive control one Tab away
            rather than under a keyboard user's finger, so an Enter pressed out of habit — or still
            held from the button that opened this — dismisses rather than deletes. */}
        <Button onClick={onClose} autoFocus>
          Cancel
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          color="error"
          disabled={running || resendUnsafe}
          // "Delete student" rather than "Delete". A screen reader moving through the dialog's
          // controls reads the label out of context, and the bare verb does not say what it acts on.
        >
          {running ? "Deleting…" : "Delete student"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
