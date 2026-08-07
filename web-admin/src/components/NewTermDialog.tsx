// The create-term form: an empty draft, the shared rules, and the failure the page hands back. The
// write itself is NOT here — `Terms.tsx` owns it, for the reason `NewDeviceDialog` records: a dialog
// that owns its own write has to block its own dismissal for as long as the request runs, and that
// lock reliably traps a keyboard user without closing the hole it was built for.
//
// What is specific to this form is the **duplicate code**, which is the mistake the operator will
// actually make. It is caught twice on purpose: against the list already on screen, which costs no
// round trip, and against the server's `409 TermCodeExists`, which is the authority and which the
// client check cannot replace — the list is a moment old, so a term created since it loaded is not
// in it. Both land on the same field, so the answer is in the same place either way.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import {
  EMPTY_TERM_DRAFT,
  NO_SCHOOL_TO_FILE_UNDER,
  NO_TERM_ERRORS,
  TERM_CODE_TAKEN,
  VALIDATED_TERM_FIELDS,
  caseCollisionWarning,
  codeCollision,
  isNoSchoolResolved,
  isTermCodeConflict,
  termFieldIdsFor,
  validateTerm,
} from "../termDraft";
import type { TermDraft, TermFieldErrors, ValidatedTermField } from "../termDraft";
import type { Term, TermWriteRequest } from "../types";
import { TermFormFields } from "./TermFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = termFieldIdsFor("new-term");
const DIALOG_TITLE_ID = "new-term-dialog-title";

/** The server decided and wrote nothing: a field refusal, or a code that is already taken. */
const HEADING_NOT_CREATED = "The term was not created";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_CREATED = "The term may have been created";

/**
 * Shown whenever Create is disabled by the failure rather than by the request in flight.
 *
 * `POST /academic/terms` carries no idempotency key, so a second press is how a school ends up with
 * two terms for one semester — and a duplicate term is quietly expensive: rosters import against one
 * of them, events resolve audiences through it, and the two halves of a semester's data then sit
 * under different ids with nothing on screen saying so. The way out is the list, which has already
 * been re-read.
 */
const RESEND_WITHHELD =
  "Create is disabled because pressing it again could create the same term twice, and a roster " +
  "imported against the wrong one of two identical terms is invisible afterwards. Close this dialog " +
  "and check the list, which is re-read after every failed attempt, before trying again.";

interface NewTermDialogProps {
  /**
   * Every term already on this page, so a duplicate code is caught before it is sent. A stale list is
   * expected and handled — see the module note.
   */
  terms: readonly Term[];
  /** Dismiss without creating: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /** Send this draft. Returns nothing — the page owns the mutation, so the outcome is the page's. */
  onSubmit: (request: TermWriteRequest) => void;
  /** The page's create is in flight. */
  running: boolean;
  /**
   * How the last create failed, or `undefined` for "no failure to show". Wrapped in an object rather
   * than passed as a bare `unknown`, because `unknown` includes `undefined`.
   */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `Terms.tsx` — so every open starts from a fresh empty draft. */
export default function NewTermDialog({
  terms,
  onClose,
  onSubmit,
  running,
  failure,
}: NewTermDialogProps) {
  const [draft, setDraft] = useState<TermDraft>(EMPTY_TERM_DRAFT);
  const [touched, setTouched] = useState<ReadonlySet<ValidatedTermField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateTerm(draft, terms);
  const errors: TermFieldErrors = checked.ok ? NO_TERM_ERRORS : checked.errors;

  /**
   * The server's own duplicate refusal, rendered **as the code field's error** rather than in the
   * alert above the form. It is a statement about one box, and the box is where someone looking for
   * it will look; `advise()` cannot produce this text, because to that taxonomy a 409 is an ordinary
   * 4xx whose advice is "send it again if the reason may have cleared".
   */
  const serverCodeRefusal = failure !== undefined && isTermCodeConflict(failure.error);

  /** The *other* 409 this route can answer, which no edit to this form can clear. */
  const noSchool = failure !== undefined && isNoSchoolResolved(failure.error);

  /**
   * Errors are computed on every keystroke and *shown* only once the user has left the field or
   * pressed Create — a form that shouts before the user has finished a word is one people learn to
   * ignore. The server's refusal is exempt: it is about a value that has already been submitted.
   */
  const errorFor = (field: ValidatedTermField): string | undefined => {
    if (field === "code" && serverCodeRefusal) return TERM_CODE_TAKEN;
    return submitAttempted || touched.has(field) ? errors[field] : undefined;
  };

  const markTouched = (field: ValidatedTermField) =>
    setTouched((current) => new Set(current).add(field));

  // Keyed on the error object, so a *second* refusal moves focus again rather than only the first
  // (WCAG 3.3.1). Without it the operator is left standing on a disabled Create button with the
  // reason three fields above them.
  const refusedError = serverCodeRefusal ? failure?.error : undefined;
  useEffect(() => {
    if (refusedError !== undefined) document.getElementById(FIELD_ID.code)?.focus();
  }, [refusedError]);

  /** Non-blocking; see `CodeCollision`. Suppressed while a real error owns the helper slot. */
  const collision = codeCollision(terms, draft.code);
  const codeWarning =
    collision?.kind === "differs-only-by-case" ? caseCollisionWarning(collision.term) : undefined;

  /** `isResendUnsafe` is `advise().retryable !== "safe"` — never a negation; see that function. */
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    // Re-validated here rather than reusing `checked` from render: they agree today, and relying on
    // that is relying on this handler never being called from a closure one render behind.
    const now = validateTerm(draft, terms);
    if (!now.ok) {
      // Focus follows the refusal: the submit button is at the bottom of the dialog, so a keyboard
      // user told "check the fields" and left standing on it would have to Shift+Tab past everything
      // to reach the problem.
      const firstInvalid = VALIDATED_TERM_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    // `onClose` unconditionally: Escape and the backdrop close this whatever the write is doing. The
    // page owns the outcome and is still here to receive it.
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      {/* A real <form>, so Enter submits from any field. `noValidate` because `required` is here to be
          announced, not enforced by a native bubble no screen reader reads reliably — one set of
          rules, mirroring the server's. */}
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Create a term</DialogTitle>

        <DialogContent>
          {/* Rendered instead of the generic write alert, not beside it. A duplicate code is already
              on the code field, and repeating it here as a headline would have the operator reading
              the same refusal twice while looking for two problems. */}
          {noSchool && (
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              <AlertTitle>{HEADING_NOT_CREATED}</AlertTitle>
              <Typography variant="body2">{describeApiError(failure?.error)}</Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {NO_SCHOOL_TO_FILE_UNDER}
              </Typography>
            </Alert>
          )}

          {failure !== undefined && !serverCodeRefusal && !noSchool && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_CREATED}
              mayHaveApplied={HEADING_MAYBE_CREATED}
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
            {/* Stated on the form rather than discovered afterwards: the create route carries no
                `isCurrent` at all, so a new term is inert until someone moves the flag. An operator
                who created a term expecting the import page to default to it would otherwise think
                the save had not worked. */}
            A new term is not made current. Use “Make current” on the list when you want the
            roster-import page to default to it — that also retires whichever term holds it now.
          </Typography>
        </DialogContent>

        <DialogActions>
          {/* Never disabled — blocking Cancel while a write ran was the fifteen-second trap. */}
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            // The courtesy; the guard that actually stops a double create is the in-flight ref inside
            // `useApiMutation`, because this attribute only lands on the next commit.
            //
            // Deliberately NOT disabled for a form with errors in it: a button that greys out without
            // saying why leaves the user hunting.
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Creating…" : "Create term"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
