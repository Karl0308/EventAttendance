// The five controls a term is described by, rendered once for both the form that creates one and the
// form that edits one.
//
// `DeviceFormFields`' reasoning applies unchanged: `POST` and `PUT` take the same body and are checked
// by the same server code, so copying these controls into a second dialog would put every label and
// every helper sentence in two places — and the helper sentences here are load-bearing, because the
// three text boxes are free text with no picker to constrain them.

import { Stack, TextField, Typography } from "@mui/material";
import type { TermDraft, TermDraftField, ValidatedTermField } from "../termDraft";

/**
 * Said under the code box, and it is the sentence that stops the code being read as a display name.
 *
 * The code is the natural key: it is what makes a term unique within the school, what a rename can
 * collide on, and what the roster-import picker lists. Its format is an institutional convention this
 * client has no opinion about, which is why the example is offered as an example rather than enforced
 * as a pattern.
 */
const CODE_HELP =
  "Required, and unique within this school. Whatever your institution calls this school year and semester — “2025-2026-1” is the shape the seeded term uses. It is stored exactly as typed, so capitalisation and punctuation are yours to decide.";

/**
 * Both dates are optional and are normally left empty — the SIS export carries no term-date columns,
 * so real imported terms have neither. Said out loud so an empty pair does not read as an omission
 * someone has to go and fill in.
 */
const DATES_HELP =
  "Both dates are optional and most terms have neither — nothing in the roster import supplies them. They are calendar dates in the school's own time zone, with no time of day, and nothing in this build schedules anything from them yet.";

interface TermFormFieldsProps {
  draft: TermDraft;
  /** A partial draft to merge. The owner holds the state; these controls only describe changes. */
  onChange: (patch: Partial<TermDraft>) => void;
  /** Field ids, from `termFieldIdsFor`. MUI builds `<label for>` and `aria-describedby` from them. */
  ids: Record<TermDraftField, string>;
  /** The error to show for a field, or `undefined` — the owner decides *when* an error is shown. */
  errorFor: (field: ValidatedTermField) => string | undefined;
  /** The user has left this field at least once. */
  onBlur: (field: ValidatedTermField) => void;
  /**
   * A non-blocking caution about the code — today, a code differing from an existing one only by
   * capitalisation. Rendered as helper text and never as an error, because it is a *probable* refusal
   * rather than a certain one and blocking on a guess would leave the operator stuck; see
   * `CodeCollision` in `termDraft.ts`. An actual error always wins the helper slot.
   */
  codeWarning: string | undefined;
}

export function TermFormFields({
  draft,
  onChange,
  ids,
  errorFor,
  onBlur,
  codeWarning,
}: TermFormFieldsProps) {
  const codeError = errorFor("code");

  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField
        id={ids.code}
        label="Term code"
        value={draft.code}
        // No trimming, no upper-casing, no reformatting anywhere on this path: the value in the box is
        // the value that is sent and the value that is stored. See `termDraft.ts`.
        onChange={(e) => onChange({ code: e.target.value })}
        onBlur={() => onBlur("code")}
        error={codeError !== undefined}
        helperText={codeError ?? codeWarning ?? CODE_HELP}
        required
        // The dialog's first focusable control, so opening it lands on the thing to type in.
        autoFocus
        // Every box here describes an academic period, never the person at the keyboard, so browser
        // autofill has nothing correct to offer.
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.schoolYear}
        label="School year"
        value={draft.schoolYear}
        onChange={(e) => onChange({ schoolYear: e.target.value })}
        onBlur={() => onBlur("schoolYear")}
        error={errorFor("schoolYear") !== undefined}
        helperText={errorFor("schoolYear") ?? "Required. The academic year this term belongs to — “2025-2026”."}
        required
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.semester}
        label="Semester"
        value={draft.semester}
        onChange={(e) => onChange({ semester: e.target.value })}
        onBlur={() => onBlur("semester")}
        error={errorFor("semester") !== undefined}
        helperText={errorFor("semester") ?? "Required. Which part of that year — “1st Semester”, “2nd Semester”, “Summer”."}
        required
        autoComplete="off"
        fullWidth
      />

      {/* `type="date"` with `shrink`, because a date input renders its own placeholder immediately and
          MUI's floating label would otherwise sit on top of it. Both are ordinary labelled inputs, so
          the native picker, keyboard entry and screen-reader announcement all come for free. */}
      <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
        <TextField
          id={ids.startsOn}
          label="Starts on"
          type="date"
          value={draft.startsOn}
          onChange={(e) => onChange({ startsOn: e.target.value })}
          onBlur={() => onBlur("startsOn")}
          error={errorFor("startsOn") !== undefined}
          helperText={errorFor("startsOn") ?? "Optional."}
          slotProps={{ inputLabel: { shrink: true } }}
          fullWidth
        />
        <TextField
          id={ids.endsOn}
          label="Ends on"
          type="date"
          value={draft.endsOn}
          onChange={(e) => onChange({ endsOn: e.target.value })}
          onBlur={() => onBlur("endsOn")}
          error={errorFor("endsOn") !== undefined}
          helperText={errorFor("endsOn") ?? "Optional. Not before the start date."}
          slotProps={{ inputLabel: { shrink: true } }}
          fullWidth
        />
      </Stack>

      <Typography variant="body2" color="text.secondary">
        {DATES_HELP}
      </Typography>
    </Stack>
  );
}
