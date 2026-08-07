// The confirmation that stands between a click and a *second* term changing.
//
// This dialog exists for one sentence, and that sentence is the whole requirement: **making a term
// current retires whichever term is current now.** Only one term per school can hold the flag — a
// filtered unique index, not a convention — so the server clears the incumbent in the same
// transaction. An operator who pressed a button about term B and later found term A demoted would
// have no way of knowing what they gave up: nothing on the list says which term *was* current once it
// is not, and the roster-import page silently starts defaulting somewhere else.
//
// So the outgoing term is named **before** the press, by `currentTermConsequence`, which is where the
// prose lives so that it can be tested without a DOM. Retiring gets the same treatment from the other
// direction: it leaves the school with no current term at all.
//
// There is deliberately **no delete anywhere on this page**. A term with a batch imported against it
// cannot be removed without data loss, so retiring is the whole of "stop using this term".

import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import { currentTermConsequence } from "../termDraft";
import type { CurrentTermIntent } from "../termDraft";
import type { Term } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "change-current-term-dialog-title";
const DIALOG_BODY_ID = "change-current-term-dialog-body";

const HEADING_NOT_CHANGED = "The current term was not changed";
const HEADING_MAYBE_CHANGED = "The current term may have changed";

/**
 * Withheld with the same caveat `RevokeDeviceKeyDialog` records: `PATCH /current` *is* idempotent —
 * setting a term that is already current, or clearing one that is not, changes nothing and answers
 * 200 — but `advise()` withholds the one-click resend because `shape` is a proxy for idempotency and
 * is exact only for POST. It errs safe, and checking is cheap here: the list says which term holds
 * the flag.
 */
const RESEND_WITHHELD =
  "This is disabled because this build cannot tell whether the flag moved. Close this and look at the " +
  "list — it is re-read after every failed attempt, and the term marked “Current” is the answer. " +
  "Repeating this is safe if it did not move.";

interface ChangeCurrentTermDialogProps {
  /** The term the operator pressed the action on. */
  term: Term;
  /** Which direction. `"retire"` is the only "stop using this term" there is — see the module note. */
  intent: CurrentTermIntent;
  /** Every term, so the one about to *lose* the flag can be named. */
  terms: readonly Term[];
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function ChangeCurrentTermDialog({
  term,
  intent,
  terms,
  onClose,
  onConfirm,
  running,
  failure,
}: ChangeCurrentTermDialogProps) {
  const { title, effect, consequence, confirmLabel } = currentTermConsequence(intent, term, terms);
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={DIALOG_BODY_ID}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>{title}</DialogTitle>

      <DialogContent>
        <DialogContentText id={DIALOG_BODY_ID}>{effect}</DialogContentText>

        {/* The second paragraph is the one this dialog exists for: what happens to something the
            operator is not looking at. Its own block rather than a clause on the first, so it is not
            skimmed past as part of the description of what was pressed. */}
        <DialogContentText sx={{ mt: 2 }}>
          <strong>{consequence}</strong>
        </DialogContentText>

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_CHANGED}
            mayHaveApplied={HEADING_MAYBE_CHANGED}
            resendWithheld={RESEND_WITHHELD}
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Cancel takes focus, not the confirm — the action that changes two rows is one Tab away
            rather than under a keyboard user's finger. */}
        <Button onClick={onClose} autoFocus>
          Cancel
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          // `warning`, not `error`: nothing is destroyed here and the move is reversible in one press.
          // Colour is never the only carrier — the label says which direction this goes.
          color={intent === "retire" ? "warning" : "primary"}
          disabled={running || resendUnsafe}
        >
          {running ? "Applying…" : confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
