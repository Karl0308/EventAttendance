// The edit-student form: a draft filled from the student, the same rules the create form is held to,
// and the failure the page hands back. The write is `Students.tsx`'s, as everywhere in this slice.
//
// What it does that the create form does not: it starts from a student, and **it has to reproduce
// that student exactly.** `PUT /students/{id}` is a full replacement, so every field goes back
// including the ones nobody touched — which is why `Student` reads the name parts, `gender` and
// `photoUrl`, and why `Students.tsx` refuses to open this form for a student it cannot round-trip.
//
// It does not render an edit *scope*, unlike `EditEventDialog`. A student has no status that forbids
// an edit: `Graduated` accepts the same body `Active` does.

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
  NO_STUDENT_ERRORS,
  VALIDATED_STUDENT_FIELDS,
  draftFromStudent,
  studentFieldIdsFor,
  validateStudent,
} from "../studentDraft";
import type { StudentDraft, StudentFieldErrors, ValidatedStudentField } from "../studentDraft";
import type { Student, StudentWriteRequest } from "../types";
import { StudentFormFields } from "./StudentFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = studentFieldIdsFor("edit-student");
const DIALOG_TITLE_ID = "edit-student-dialog-title";
const DERIVED_HEADING_ID = "edit-student-derived-heading";

/** The server decided and wrote nothing: a §4.3 field refusal, or a 409 on the student number. */
const HEADING_NOT_SAVED = "The change was not saved";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_SAVED = "The change may have been saved";

/**
 * Shown when Save is disabled by the failure rather than by the request in flight.
 *
 * It does **not** say "pressing it again could save it twice", which would be false: `PUT` is
 * idempotent, so a second send of the same body lands on the same row with the same values. What is
 * true is the thing worth saying — this build cannot tell whether the first one landed. `advise()`
 * withholds the one-click resend here because `shape` is a proxy for idempotency and is exact only for
 * `POST`; it errs safe, and the sentence stays honest about why.
 */
const RESEND_WITHHELD =
  "Save is disabled because this build cannot tell whether the change was applied. Close this dialog " +
  "and read the student in the list — it is re-read after every failed attempt — before sending it " +
  "again.";

interface EditStudentDialogProps {
  /** The student as the page last read them. Snapshotted on open; see `baseline` below. */
  student: Student;
  /** Dismiss without saving: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /** Send this body. Returns nothing — the page owns the mutation and routes the outcome. */
  onSubmit: (request: StudentWriteRequest) => void;
  /** The page's update is in flight. */
  running: boolean;
  /** How the last save failed, or `undefined`. Wrapped, because `unknown` includes `undefined`. */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `Students.tsx` — so every open starts from the student as they are. */
export default function EditStudentDialog({
  student,
  onClose,
  onSubmit,
  running,
  failure,
}: EditStudentDialogProps) {
  /**
   * The student as they were when this form opened, held rather than read from the prop on every
   * render.
   *
   * The page already hands down a snapshot — the open student is set once and is not refreshed by the
   * list's `reload()` — so this holds today whether or not the state exists. It is here to make that
   * independent of how the page chooses to hold it: the list is re-read after a failed save, and if
   * that read were ever wired through to this prop, following it would overwrite what the user has
   * typed with what the server currently holds, at exactly the moment they are trying to correct it.
   */
  const [baseline] = useState<Student>(student);
  const [draft, setDraft] = useState<StudentDraft>(() => draftFromStudent(baseline));
  const [touched, setTouched] = useState<ReadonlySet<ValidatedStudentField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateStudent(draft);
  const errors: StudentFieldErrors = checked.ok ? NO_STUDENT_ERRORS : checked.errors;

  const errorFor = (field: ValidatedStudentField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedStudentField) =>
    setTouched((current) => new Set(current).add(field));

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateStudent(draft);
    if (!now.ok) {
      // Focus follows the refusal, for the reason `NewStudentDialog` records (WCAG 3.3.1).
      const firstInvalid = VALIDATED_STUDENT_FIELDS.find(
        (field) => now.errors[field] !== undefined,
      );
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        {/* Named, not "Edit student": a screen-reader user is given the dialog's label on open, and
            "which student is this about" is the one thing they cannot check by glancing behind it. */}
        <DialogTitle id={DIALOG_TITLE_ID}>Edit {baseline.fullName}</DialogTitle>

        <DialogContent>
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <StudentFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            // From the baseline rather than from a live prop, so what the panel says the academic
            // records hold cannot change under the user mid-edit. Read straight through as three
            // values: this component never sees a shape it could put one of them in a draft from.
            derived={{
              course: baseline.course,
              yearLevel: baseline.yearLevel,
              section: baseline.section,
            }}
            derivedHeadingId={DERIVED_HEADING_ID}
          />
        </DialogContent>

        <DialogActions>
          {/* Never disabled, for the reason the create dialog records: a focus-trapped dialog whose
              only two controls are both dead is a keyboard user with nowhere to go. */}
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
