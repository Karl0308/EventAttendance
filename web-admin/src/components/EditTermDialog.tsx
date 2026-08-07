// The edit-term form — `NewTermDialog`'s sibling, deliberately parallel to it, and differing in
// exactly three things.
//
//   1. It starts from the term rather than from an empty draft, and `PUT` is a **full replacement**,
//      so every box is sent whether it was touched or not. Nothing here may default a field.
//   2. The duplicate check excludes **this** term: a term is not a duplicate of itself, and an edit
//      that leaves the code alone has to stay saveable. The exclusion is done here rather than by the
//      page so the invariant sits next to the check it guards.
//   3. `isCurrent` is untouched by this route, and the form says so — an operator editing the current
//      term must not have to wonder whether saving a typo fix moved the flag.
//
// The write is not here; `Terms.tsx` owns it, for the reason `NewTermDialog` records.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  NO_TERM_ERRORS,
  TERM_CODE_TAKEN,
  VALIDATED_TERM_FIELDS,
  caseCollisionWarning,
  codeCollision,
  draftFromTerm,
  isTermCodeConflict,
  termFieldIdsFor,
  validateTerm,
} from "../termDraft";
import type { TermDraft, TermFieldErrors, ValidatedTermField } from "../termDraft";
import type { Term, TermWriteRequest } from "../types";
import { TermFormFields } from "./TermFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = termFieldIdsFor("edit-term");
const DIALOG_TITLE_ID = "edit-term-dialog-title";

/** The server decided and wrote nothing: a field refusal, a 404, or a code that is already taken. */
const HEADING_NOT_SAVED = "The change was not saved";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_SAVED = "The change may have been saved";

/**
 * Unlike the create form, a repeated `PUT` is not a duplicate — it is the same replacement again. So
 * this says the narrower true thing: the outcome is unknown, and the list is where it is settled.
 */
const RESEND_WITHHELD =
  "Save is disabled because this build cannot tell whether the change was applied. A second save " +
  "would not create anything — it is the same replacement — but it would overwrite whatever is there " +
  "now, including an edit someone else made in between. Close this dialog and check the list, which " +
  "is re-read after every failed attempt.";

/** What this route does not do, said where the operator can act on it. */
const CURRENT_FLAG_UNTOUCHED =
  "Saving this does not change which term is current. That is its own action on the list, because " +
  "only one term per school can be current and moving the flag retires whichever term holds it.";

/**
 * What a rename does not rewrite. Stale wording rather than a wrong audience — derived group names
 * embed the code at projection time and keep the old text until the next import re-runs — but an
 * operator who renames a code and then sees the old one on an event's sections deserves to have been
 * told, rather than to file a bug.
 *
 * The last sentence is about the one case where "stale" understates it: if two terms swap codes, the
 * old text on this term's groups is now the name of a *different* real term rather than of nothing.
 * The ids are unaffected either way, so the audience is still right — but the label is one an operator
 * would act on, which the ordinary stale case is not.
 */
const RENAME_NOTE =
  "The code can be changed. Section and course groups built by an earlier import keep the old code in " +
  "their display names until the next import runs; they still point at this term, so audiences and " +
  "counts are unaffected. If you swap codes between two terms, re-run the import for both — until you " +
  "do, those old display names read as the name of the other term.";

interface EditTermDialogProps {
  /** The term being edited. Its id is what the exclusion above is keyed on. */
  term: Term;
  /** Every term on the page, including this one — the exclusion happens here. */
  terms: readonly Term[];
  onClose: () => void;
  onSubmit: (request: TermWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

/** Mounted only while open, so every open starts from the term as it was last read. */
export default function EditTermDialog({
  term,
  terms,
  onClose,
  onSubmit,
  running,
  failure,
}: EditTermDialogProps) {
  const [draft, setDraft] = useState<TermDraft>(() => draftFromTerm(term));
  const [touched, setTouched] = useState<ReadonlySet<ValidatedTermField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  // Every *other* term. See the module note: without this a term collides with itself and the form
  // refuses a save that changes only the semester's spelling.
  const others = terms.filter((other) => other.id !== term.id);

  const checked = validateTerm(draft, others);
  const errors: TermFieldErrors = checked.ok ? NO_TERM_ERRORS : checked.errors;

  /** The server's own duplicate refusal, on the field it is about. See `NewTermDialog`. */
  const serverCodeRefusal = failure !== undefined && isTermCodeConflict(failure.error);

  const errorFor = (field: ValidatedTermField): string | undefined => {
    if (field === "code" && serverCodeRefusal) return TERM_CODE_TAKEN;
    return submitAttempted || touched.has(field) ? errors[field] : undefined;
  };

  const markTouched = (field: ValidatedTermField) =>
    setTouched((current) => new Set(current).add(field));

  const refusedError = serverCodeRefusal ? failure?.error : undefined;
  useEffect(() => {
    if (refusedError !== undefined) document.getElementById(FIELD_ID.code)?.focus();
  }, [refusedError]);

  const collision = codeCollision(others, draft.code);
  const codeWarning =
    collision?.kind === "differs-only-by-case" ? caseCollisionWarning(collision.term) : undefined;

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateTerm(draft, others);
    if (!now.ok) {
      const firstInvalid = VALIDATED_TERM_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Edit {term.code}</DialogTitle>

        <DialogContent>
          {failure !== undefined && !serverCodeRefusal && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <TermFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            codeWarning={codeWarning}
          />

          <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
            {RENAME_NOTE}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
            {CURRENT_FLAG_UNTOUCHED}
          </Typography>
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : "Save changes"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
