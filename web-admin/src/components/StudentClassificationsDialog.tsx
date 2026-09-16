// One student's classifications: one picker per axis (Task 3), rather than one flat multi-select.
//
// ---------------------------------------------------------------------------------------------
// Why one picker per axis
// ---------------------------------------------------------------------------------------------
//
// QA answered Q2 that this is multiple selection, and the data model is more specific than "pick
// several": `UX_StudentClassifications_Student_Axis` caps a person at **one classification per axis**
// while leaving them free to hold several axes at once. A flat multi-select over all eight seeded
// values cannot express that constraint — nothing would stop someone ticking both `NAP` and `ACAD`,
// which the server would then refuse (or worse, silently accept as two calls where the second replaces
// the first, which is what `PUT` actually does, and a flat control gives no way to *see* that). Four
// axis-scoped controls make the constraint the shape of the form instead of a rule the operator has to
// already know.
//
// ---------------------------------------------------------------------------------------------
// Why this is a separate dialog rather than a section of the Add/Edit student form
// ---------------------------------------------------------------------------------------------
//
// `StudentDto.classifications` is read-only — assignment is `PUT`/`DELETE
// /students/{studentId}/classifications/{classificationId}`, one call per axis, and never a field of
// `StudentWriteRequest`. So an Add/Edit form that "saved" classifications inline would have two
// different save mechanisms behind one Save button: one `PUT /students/{id}` for the student's own
// fields, and up to four more requests — each independently able to 409 — for whatever axes changed.
// That is the same shape `StudentCardsDialog` was already split out to avoid for cards, for the same
// reason: a single button that can partially fail needs its own space to say which part failed, not a
// shared alert at the bottom of an unrelated form.
//
// It also settles the **Add** path cleanly instead of awkwardly: there is no student id to assign a
// classification against until `POST /students` has answered, so `NewStudentDialog` carries no
// classification section at all — precisely how it already carries no card-attach section. The row
// action that opens this dialog is the same "RFID cards" pattern: available once the student exists,
// not before.
//
// The write itself lives in `Students.tsx`, as everywhere in this slice.

import type { SelectChangeEvent } from "@mui/material";
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Typography,
} from "@mui/material";
import { api } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import { CLASSIFICATION_AXES } from "../types";
import type { Classification, Student, StudentClassification } from "../types";
import { useApiResource } from "../useApiResource";
import { ErrorState, LoadingState } from "./ResourceStates";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "student-classifications-dialog-title";

/** The Select's sentinel for "nothing in this axis" — safe because a classification id is never "". */
const NONE_VALUE = "";

const HEADING_NOT_ASSIGNED = "The classification was not assigned";
const HEADING_MAYBE_ASSIGNED = "The classification may have been assigned";
const HEADING_NOT_CLEARED = "The classification was not cleared";
const HEADING_MAYBE_CLEARED = "The classification may have been cleared";

/**
 * Both writes ARE idempotent for a repeat of the exact same request — a re-sent `PUT` lands on the
 * same row with the same value, and a re-sent `DELETE` finds nothing left to remove and answers 404
 * rather than removing someone else's later change. But `advise()`'s `retryable` is a proxy read from
 * `shape`, not a per-endpoint fact, and it errs safe: a `network`/`malformed`/5xx failure on either
 * write still withholds the one-click resend, because this build cannot *tell* the difference between
 * "safe to repeat" and "already applied" from the failure alone. These two sentences are what explain
 * why every axis stays disabled until the dialog is closed and reopened in that case.
 */
const ASSIGN_RESEND_WITHHELD =
  "Choosing a classification is disabled because this build cannot tell whether the last choice was " +
  "applied. Close this dialog and reopen it — the set is re-read on every open — before trying again.";
const CLEAR_RESEND_WITHHELD =
  "Clearing is disabled because this build cannot tell whether the last clear was applied. Close this " +
  "dialog and reopen it — the set is re-read on every open — before trying again.";

interface StudentClassificationsDialogProps {
  /** The student, as freshly as the page can supply them — the dialog is about their live set. */
  student: Student;
  onClose: () => void;
  /** Which axis's control started the write currently in flight, so only that one shows "Saving…". */
  actingAxis: string | undefined;
  assign: {
    running: boolean;
    failure: { error: unknown } | undefined;
    submit: (target: Classification, previous: StudentClassification | undefined) => void;
  };
  clear: {
    running: boolean;
    failure: { error: unknown } | undefined;
    submit: (previous: StudentClassification) => void;
  };
}

export default function StudentClassificationsDialog({
  student,
  onClose,
  actingAxis,
  assign,
  clear,
}: StudentClassificationsDialogProps) {
  // Fetched here rather than by the page: the vocabulary is only ever needed while this dialog is
  // open, and mounting it fresh on every open (the dialog is conditionally rendered in `Students.tsx`,
  // as every other dialog in this slice) keeps a retire/rename made elsewhere from going stale behind
  // a dialog left open — the next open re-reads it.
  const vocabulary = useApiResource(() => api.listClassifications(), []);

  // Any write in flight disables every axis, not just the one it started on. The two mutations are
  // shared across all four axes (as `StudentCardsDialog` shares one attach and one detach across every
  // card), and `useApiMutation`'s own in-flight guard drops a second call as `"ignored"` with no
  // feedback — so letting a second axis's control stay live would let a click be silently dropped
  // rather than visibly queued or refused. Nothing about the four axes' *data* conflicts (each is a
  // different database row), only the UI's single-mutation-at-a-time shape does.
  //
  // A failure whose resend is unsafe keeps every axis disabled afterwards too, for the reason
  // `ASSIGN_RESEND_WITHHELD`/`CLEAR_RESEND_WITHHELD` give: this build cannot tell whether the failed
  // write actually landed, so a further click from any axis is withheld until the dialog is reopened.
  const assignResendUnsafe = assign.failure !== undefined && isResendUnsafe(assign.failure.error);
  const clearResendUnsafe = clear.failure !== undefined && isResendUnsafe(clear.failure.error);
  const busy = assign.running || clear.running || assignResendUnsafe || clearResendUnsafe;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <DialogTitle id={DIALOG_TITLE_ID}>Classifications — {student.fullName}</DialogTitle>

      <DialogContent>
        {assign.failure !== undefined && (
          <WriteFailureAlert
            error={assign.failure.error}
            notApplied={HEADING_NOT_ASSIGNED}
            mayHaveApplied={HEADING_MAYBE_ASSIGNED}
            resendWithheld={ASSIGN_RESEND_WITHHELD}
          />
        )}
        {clear.failure !== undefined && (
          <WriteFailureAlert
            error={clear.failure.error}
            notApplied={HEADING_NOT_CLEARED}
            mayHaveApplied={HEADING_MAYBE_CLEARED}
            resendWithheld={CLEAR_RESEND_WITHHELD}
          />
        )}

        {vocabulary.status === "loading" && <LoadingState label="Loading classifications…" />}

        {vocabulary.status === "error" && (
          <ErrorState subject="the classification vocabulary" error={vocabulary.error} onRetry={vocabulary.reload} />
        )}

        {vocabulary.status === "ready" && (
          <Stack spacing={3}>
            {CLASSIFICATION_AXES.map((axis) => (
              <AxisPicker
                key={axis}
                axis={axis}
                held={student.classifications.find((c) => c.axis === axis)}
                options={vocabulary.data.filter((c) => c.axis === axis)}
                disabled={busy}
                saving={(assign.running || clear.running) && actingAxis === axis}
                onAssign={(target, previous) => assign.submit(target, previous)}
                onClear={(previous) => clear.submit(previous)}
              />
            ))}
          </Stack>
        )}
      </DialogContent>

      <DialogActions>
        <Button onClick={onClose}>Done</Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * One axis: what this person currently holds on it, if anything, and a picker among the active
 * vocabulary for it. A held-but-retired classification is shown but excluded from the picker's own
 * options — it cannot be re-selected, only cleared or replaced by choosing something else.
 */
function AxisPicker({
  axis,
  held,
  options,
  disabled,
  saving,
  onAssign,
  onClear,
}: {
  axis: string;
  held: StudentClassification | undefined;
  options: readonly Classification[];
  disabled: boolean;
  saving: boolean;
  onAssign: (target: Classification, previous: StudentClassification | undefined) => void;
  onClear: (previous: StudentClassification) => void;
}) {
  const selectId = `classification-axis-${axis}`;
  const labelId = `${selectId}-label`;

  // The Select's own value must be one of its rendered options or MUI logs a warning and shows
  // nothing selected. A retired-but-held classification is deliberately NOT one of `options` (the
  // vocabulary read excludes retired entries), so the control shows "— none —" underneath the
  // separate retired notice above it rather than a phantom selection.
  const selectValue = held !== undefined && held.isActive ? held.classificationId : NONE_VALUE;

  const change = (event: SelectChangeEvent<string>) => {
    const nextId = event.target.value;
    if (nextId === NONE_VALUE) {
      if (held !== undefined) onClear(held);
      return;
    }
    const target = options.find((c) => c.id === nextId);
    // Cannot happen from the rendered `<MenuItem>`s themselves, but `options` can change between
    // render and the event reaching here if a background re-read landed in between — fail quietly
    // into "do nothing" rather than send a request built from a value that is no longer offered.
    if (target !== undefined) onAssign(target, held);
  };

  return (
    <Box>
      <Typography variant="subtitle2" component="h3" gutterBottom>
        {axis}
        {saving && (
          <Typography component="span" variant="caption" color="text.secondary" sx={{ ml: 1 }}>
            <CircularProgress size={12} sx={{ mr: 0.5, verticalAlign: "middle" }} />
            Saving…
          </Typography>
        )}
      </Typography>

      {held !== undefined && !held.isActive && (
        <Alert severity="warning" role="status" sx={{ mb: 1 }}>
          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
            <Typography variant="body2">
              Currently holds a <strong>retired</strong> classification: {held.name}.
            </Typography>
            <Chip size="small" label="Retired" />
            <Button
              size="small"
              color="error"
              disabled={disabled}
              onClick={() => onClear(held)}
              aria-label={`Clear retired classification ${held.name} on ${axis}`}
            >
              Clear
            </Button>
          </Stack>
        </Alert>
      )}

      <FormControl fullWidth size="small" disabled={disabled}>
        {/*
          `shrink` is not cosmetic and must stay paired with the `displayEmpty` below. MUI force-
          passes `notched: true` to the outlined input whenever `displayEmpty` is set, but nothing
          feeds `displayEmpty` into the label's own shrink calculation — that reads `filled`, which
          is false for an empty value. Unpaired, the notch is cut open with the full-size label
          sitting inside the box directly over the placeholder.
        */}
        <InputLabel id={labelId} shrink>
          {axis}
        </InputLabel>
        <Select
          labelId={labelId}
          id={selectId}
          label={axis}
          value={selectValue}
          onChange={change}
          // Without this, MUI renders the closed control's VALUE SPAN empty whenever the value is
          // the empty sentinel, so the "— none —" below is only ever seen in the open dropdown.
          // Note what this is *not*: the control was never blank — the axis label sat in it, as an
          // untouched outlined field does. What is bought here is that an axis nobody holds says so
          // in the same place a held one names its value, which is the state 34 people in the
          // roster are in. Keep it paired with the label's `shrink` above; alone it cuts the notch
          // open and draws the label over this placeholder.
          displayEmpty
        >
          <MenuItem value={NONE_VALUE}>
            <em>— none —</em>
          </MenuItem>
          {options.map((option) => (
            <MenuItem key={option.id} value={option.id}>
              {option.name}
            </MenuItem>
          ))}
        </Select>
      </FormControl>
    </Box>
  );
}
