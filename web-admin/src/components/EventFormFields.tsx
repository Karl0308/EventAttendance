// The eight controls an event is described by, rendered once for both the form that creates one and
// the form that edits one.
//
// The two forms differ in their title, their submit button, what they say before the fields, and which
// fields the server will accept a change to — none of which is a control. Copying the controls
// themselves into a second dialog would have put every label, every helper sentence and every
// narrowing `onChange` in two places, so a limit corrected in one form would go on being wrong in the
// other. The rules they enforce already live in one place (`eventDraft.ts`); this is the rest of it.

import {
  Box,
  Checkbox,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { ATTENDANCE_MODES } from "../types";
import {
  ATTENDANCE_MODE_LABELS,
  DESCRIPTION_MAX_LENGTH,
  LOCATION_MAX_LENGTH,
  MAX_GRACE_MINUTES,
  MIN_GRACE_MINUTES,
  NAME_MAX_LENGTH,
  knownMode,
} from "../eventDraft";
import type { Draft, DraftField, ValidatedField } from "../eventDraft";

/**
 * Said on every locked field, beside the alert the dialog puts above them. The alert carries the
 * reason in full; this is what a user who tabbed straight into the field is told without having to go
 * looking for it.
 */
const LOCKED_HELP = "Locked — see the note above.";

interface EventFormFieldsProps {
  draft: Draft;
  /** A partial draft to merge. The owner holds the state; these controls only describe changes. */
  onChange: (patch: Partial<Draft>) => void;
  /** Field ids, from `fieldIdsFor`. MUI builds `<label for>` and `aria-describedby` out of them. */
  ids: Record<DraftField, string>;
  /** The error to show for a field, or `undefined` — the owner decides when an error is shown. */
  errorFor: (field: ValidatedField) => string | undefined;
  /** The user has left this field at least once. */
  onBlur: (field: ValidatedField) => void;
  /**
   * The fields the server will refuse to see changed, given the event's status. Empty for a create
   * and for a `Draft`/`Open` event; the five attendance-rule fields for a `Cancelled` one.
   *
   * The values are still sent — `PUT` is a full replacement — and `validate` sends the instants back
   * byte-for-byte when the box was not touched, which is what keeps a locked event's save from being
   * refused for a field the user never went near.
   */
  locked: ReadonlySet<DraftField>;
}

export function EventFormFields({
  draft,
  onChange,
  ids,
  errorFor,
  onBlur,
  locked,
}: EventFormFieldsProps) {
  /**
   * Helper text, in precedence order: the lock, then the error, then the standing hint. The lock is
   * first because a disabled field cannot be corrected, so telling the user how to correct it would
   * be the least useful of the three — and a locked field is valid by construction anyway: the page
   * only opens this form for an event whose locked values it can round-trip.
   */
  const helper = (field: DraftField, hint: string, error?: string): string =>
    locked.has(field) ? LOCKED_HELP : (error ?? hint);

  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField
        id={ids.name}
        label="Name"
        value={draft.name}
        onChange={(e) => onChange({ name: e.target.value })}
        onBlur={() => onBlur("name")}
        error={errorFor("name") !== undefined}
        helperText={helper("name", `Required. Up to ${NAME_MAX_LENGTH} characters.`, errorFor("name"))}
        required
        // The dialog's first focusable control, so opening it lands on the thing to type in.
        autoFocus
        disabled={locked.has("name")}
        fullWidth
      />

      <TextField
        id={ids.startAt}
        label="Starts"
        type="datetime-local"
        value={draft.startAt}
        onChange={(e) => onChange({ startAt: e.target.value })}
        onBlur={() => onBlur("startAt")}
        error={errorFor("startAt") !== undefined}
        helperText={helper("startAt", "Your local time.", errorFor("startAt"))}
        required
        // A datetime input always shows its placeholder text, so the label has nowhere to sit unless
        // it is shrunk from the start.
        slotProps={{ inputLabel: { shrink: true } }}
        disabled={locked.has("startAt")}
        fullWidth
      />

      <TextField
        id={ids.endAt}
        label="Ends"
        type="datetime-local"
        value={draft.endAt}
        onChange={(e) => onChange({ endAt: e.target.value })}
        onBlur={() => onBlur("endAt")}
        error={errorFor("endAt") !== undefined}
        helperText={helper("endAt", "Must be after the start.", errorFor("endAt"))}
        required
        slotProps={{ inputLabel: { shrink: true } }}
        disabled={locked.has("endAt")}
        fullWidth
      />

      <TextField
        id={ids.location}
        label="Location"
        value={draft.location}
        onChange={(e) => onChange({ location: e.target.value })}
        onBlur={() => onBlur("location")}
        error={errorFor("location") !== undefined}
        helperText={helper(
          "location",
          `Optional. Up to ${LOCATION_MAX_LENGTH} characters.`,
          errorFor("location"),
        )}
        disabled={locked.has("location")}
        fullWidth
      />

      <TextField
        id={ids.attendanceMode}
        label="Attendance mode"
        select
        value={draft.attendanceMode}
        // Narrowed against the same array the options are built from, rather than asserted back to
        // the union. The assertion would compile and would be a lie the moment anyone rendered a
        // third `MenuItem` by hand; the lookup cannot miss for a value that came from these options,
        // and if it somehow did it leaves the current mode standing rather than putting a value in
        // the draft that the server is guaranteed to refuse.
        onChange={(e) => {
          const chosen = knownMode(e.target.value);
          if (chosen !== undefined) onChange({ attendanceMode: chosen });
        }}
        helperText={locked.has("attendanceMode") ? LOCKED_HELP : undefined}
        disabled={locked.has("attendanceMode")}
        fullWidth
      >
        {ATTENDANCE_MODES.map((mode) => (
          <MenuItem key={mode} value={mode}>
            {ATTENDANCE_MODE_LABELS[mode]}
          </MenuItem>
        ))}
      </TextField>

      <TextField
        id={ids.graceMinutes}
        label="Grace period (minutes)"
        type="number"
        value={draft.graceMinutes}
        onChange={(e) => onChange({ graceMinutes: e.target.value })}
        onBlur={() => onBlur("graceMinutes")}
        error={errorFor("graceMinutes") !== undefined}
        helperText={helper(
          "graceMinutes",
          `Arrivals within this many minutes of the start count as Present. ${MIN_GRACE_MINUTES}–${MAX_GRACE_MINUTES}.`,
          errorFor("graceMinutes"),
        )}
        // Advisory only — a number input can still be typed into out of range, and `validate` is what
        // actually refuses it. These give the spinner sensible steps and the browser something to
        // hint with.
        slotProps={{ htmlInput: { min: MIN_GRACE_MINUTES, max: MAX_GRACE_MINUTES, step: 1 } }}
        disabled={locked.has("graceMinutes")}
        fullWidth
      />

      <TextField
        id={ids.description}
        label="Description"
        value={draft.description}
        onChange={(e) => onChange({ description: e.target.value })}
        onBlur={() => onBlur("description")}
        error={errorFor("description") !== undefined}
        helperText={helper(
          "description",
          `Optional. Up to ${DESCRIPTION_MAX_LENGTH} characters.`,
          errorFor("description"),
        )}
        multiline
        minRows={2}
        disabled={locked.has("description")}
        fullWidth
      />

      <Box>
        <FormControlLabel
          control={
            <Checkbox
              id={ids.requireRegistration}
              checked={draft.requireRegistration}
              onChange={(e) => onChange({ requireRegistration: e.target.checked })}
              disabled={locked.has("requireRegistration")}
            />
          }
          label="Require registration"
        />
        {/* A checkbox has no `helperText` slot, so the lock is said beside it rather than not at
            all — the one control where the pattern the other seven use is unavailable. */}
        {locked.has("requireRegistration") && (
          <Typography variant="body2" color="text.secondary">
            {LOCKED_HELP}
          </Typography>
        )}
      </Box>
    </Stack>
  );
}
