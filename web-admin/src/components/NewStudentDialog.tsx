// The create-student form: an empty draft, the shared rules, and the failure the page hands back. The
// write itself is NOT here — `Students.tsx` owns it, for the reason `NewEventDialog` records at
// length: a dialog that owns its own write has to block its own dismissal for as long as the request
// runs, and that lock reliably traps a keyboard user without closing the hole it was built for.
//
// A `Dialog` rather than a route. Adding a student is a short interruption of the roster — the user is
// looking at the list, decides to add someone, and wants to be back at the list with the new row on
// it — and a `/students/new` route would give that interruption a URL, browser history and a back
// button, each of which is a way to lose a half-typed form.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  EMPTY_STUDENT_DRAFT,
  NO_STUDENT_ERRORS,
  VALIDATED_STUDENT_FIELDS,
  studentFieldIdsFor,
  validateStudent,
} from "../studentDraft";
import type { StudentDraft, StudentFieldErrors, ValidatedStudentField } from "../studentDraft";
import type { StudentWriteRequest } from "../types";
import { StudentFormFields } from "./StudentFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = studentFieldIdsFor("new-student");
const DIALOG_TITLE_ID = "new-student-dialog-title";
const DERIVED_HEADING_ID = "new-student-derived-heading";

/** The server decided and wrote nothing: a §4.3 field refusal, or a 409 on the student number. */
const HEADING_NOT_CREATED = "The student was not created";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_CREATED = "The student may have been created";

/**
 * Shown whenever Create is disabled by the failure rather than by the request in flight.
 *
 * There is deliberately no one-click resend for `may-duplicate`. `POST /students` carries no
 * idempotency key, so a second press is how a roster ends up with the same person twice — and unlike a
 * duplicate event, the second row takes a student number the first one needs, so the mistake is not
 * even cleanly repairable from this app. What the dialog offers instead is the truth and the way out:
 * the list behind has already been re-read.
 */
const RESEND_WITHHELD =
  "Create is disabled because pressing it again could add the same student twice. Close this dialog " +
  "and search the list for the student number — it is re-read after every failed attempt — before " +
  "trying again.";

interface NewStudentDialogProps {
  /** Dismiss without creating: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /**
   * Send this draft. Returns nothing on purpose — the page owns the mutation, so the outcome is the
   * page's to route, and a promise returned here is one a submit handler would drop.
   */
  onSubmit: (request: StudentWriteRequest) => void;
  /** The page's create is in flight. */
  running: boolean;
  /**
   * How the last create failed, or `undefined` for "no failure to show". Wrapped in an object rather
   * than passed as a bare `unknown`, because `unknown` includes `undefined`: "failed, with an
   * undefined reason" and "has not failed" would otherwise be the same value.
   */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `Students.tsx` — so every open starts from a fresh empty draft. */
export default function NewStudentDialog({
  onClose,
  onSubmit,
  running,
  failure,
}: NewStudentDialogProps) {
  const [draft, setDraft] = useState<StudentDraft>(EMPTY_STUDENT_DRAFT);
  // A set, not a map of booleans: "has been left at least once" is membership, and there is no third
  // state for it to be in.
  const [touched, setTouched] = useState<ReadonlySet<ValidatedStudentField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateStudent(draft);
  const errors: StudentFieldErrors = checked.ok ? NO_STUDENT_ERRORS : checked.errors;

  /**
   * Errors are computed on every keystroke and *shown* only once the user has left the field or
   * pressed Create. Showing them as they type turns "Mari" into a red box complaining a name is
   * required, and a form that shouts before the user has finished a word is one people learn to ignore.
   */
  const errorFor = (field: ValidatedStudentField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedStudentField) =>
    setTouched((current) => new Set(current).add(field));

  /**
   * Whether pressing Create again is a thing this dialog is willing to offer. `isResendUnsafe` is
   * `advise().retryable !== "safe"` — never a negation, because `"may-duplicate"` is truthy and a
   * negation would go on offering the button that adds the second student.
   */
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    // Re-validated here rather than reusing `checked` from render: they agree today, and relying on
    // that is relying on this handler never being called from a closure one render behind.
    const now = validateStudent(draft);
    if (!now.ok) {
      // Focus follows the refusal. The Create button is at the bottom of the dialog, so a keyboard
      // user told "check the fields" and left standing on it would have to Shift+Tab past everything
      // to reach the problem, and a screen-reader user is told nothing at all because the field errors
      // appear above where they are standing (WCAG 3.3.1).
      const firstInvalid = VALIDATED_STUDENT_FIELDS.find(
        (field) => now.errors[field] !== undefined,
      );
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    // Handed to the page and forgotten. The double-submit race is still closed — by the in-flight ref
    // inside `useApiMutation`, which is now the page's — and a second press while one is in flight is
    // dropped there, not queued.
    onSubmit(now.request);
  };

  return (
    // `onClose` unconditionally: Escape and the backdrop close this whatever the write is doing. The
    // page is what the outcome belongs to now, and it is still here to receive it.
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      {/* A real <form>: Enter submits from any field without a keydown handler of our own, and the
          controls are grouped as one thing rather than as eight that happen to sit together.

          `noValidate` because `required` and `type="email"` are here to be *announced* — not to be
          enforced by the browser. Left on, Chrome would block submit with a transient native bubble
          that no screen reader reads reliably, and it would enforce an e-mail format rule the server
          does not have. One set of rules, in one place, mirroring the server's. */}
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>New student</DialogTitle>

        <DialogContent>
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_CREATED}
              mayHaveApplied={HEADING_MAYBE_CREATED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <StudentFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            // No student yet, so no enrolments and nothing to show. The panel says so rather than
            // rendering three dashes that read as "we looked and there is nothing".
            derived={undefined}
            derivedHeadingId={DERIVED_HEADING_ID}
          />
        </DialogContent>

        <DialogActions>
          {/* Never disabled. Blocking this while a create ran was the fifteen-second trap: a
              focus-trapped dialog whose only two controls are both dead is a keyboard user with
              nowhere to go, and it bought nothing — the page owns the write and reports it either
              way. */}
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            // `disabled` is the courtesy; the guard that actually stops a double create is the
            // in-flight ref inside `useApiMutation`, because this attribute only lands on the next
            // commit and two clicks can happen inside one.
            //
            // Deliberately NOT disabled for a form with errors in it: a button that greys out without
            // saying why leaves the user hunting, where a press that answers "this field, this limit"
            // and puts the cursor in it says exactly what to fix.
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Creating…" : "Create student"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
