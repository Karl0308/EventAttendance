// The eight controls a student is described by, plus the three values that are deliberately NOT
// controls — rendered once for the form that creates a student and the form that edits one.
//
// `EventFormFields`'s sibling, and the argument is the same: the two forms differ in their title,
// their submit button and what they say before the fields, none of which is a control, so copying the
// controls into a second dialog would put every label and every helper sentence in two places. The
// rules they enforce live in `studentDraft.ts`; this is the rest of it.
//
// What is different from the events pair: there is no `locked` set. A student has no state that
// forbids an edit — `PUT /students/{id}` takes the same body from every status — so the only fields
// this form cannot change are the three that no request may ever carry, and those are not disabled
// inputs. See `DERIVED_NOTE`.

import { Box, MenuItem, Stack, TextField, Typography } from "@mui/material";
import { STUDENT_STATUSES } from "../types";
import {
  EMAIL_MAX_LENGTH,
  GENDER_MAX_LENGTH,
  PERSON_NAME_MAX_LENGTH,
  PHOTO_URL_MAX_LENGTH,
  STUDENT_NUMBER_MAX_LENGTH,
  STUDENT_STATUS_LABELS,
  knownStatus,
} from "../studentDraft";
import type { StudentDraft, StudentDraftField, ValidatedStudentField } from "../studentDraft";

/** What a derived value reads as when the student has none. */
const NO_VALUE = "—";

/**
 * Why course, year level and section are shown and cannot be typed into.
 *
 * Said in full rather than left to three greyed boxes. The standing rule in this codebase is that a
 * control which greys out without saying why leaves the user hunting for a permission they do not
 * lack — and here they would be hunting for one that does not exist, because these are not the
 * student's fields at all. The reason is ADR-001 D-2 and it is a fact about the data rather than about
 * access: the triple is single-valued, a student can sit in more than one section at once, and 12 of
 * the 52 in the real roster do. The last sentence is the actionable half — an explanation that does
 * not say where the value *can* be changed is a dead end with better manners.
 */
const DERIVED_NOTE =
  "These are a display cache, refreshed from the academic records — not fields on the student, and " +
  "the API refuses a write that carries one. A student can be enrolled in more than one section at " +
  "a time (12 of the 52 in the current roster are), so a single course, year and section cannot " +
  "describe them and must never be treated as the full answer. To change them, change the " +
  "enrolment: enrolments decide the course and section, the term record decides the programme, " +
  "college and year level.";

/** A student who does not exist yet has no enrolments, which is a different sentence. */
const DERIVED_NOTE_NEW =
  "A new student has no academic records yet, so there is nothing to show here. Course, year level " +
  "and section are a display cache refreshed from enrolments and term records — they are never " +
  "written through this form, and the API refuses a request that carries one.";

interface StudentFormFieldsProps {
  draft: StudentDraft;
  /** A partial draft to merge. The owner holds the state; these controls only describe changes. */
  onChange: (patch: Partial<StudentDraft>) => void;
  /** Field ids, from `studentFieldIdsFor`. MUI builds `<label for>` and `aria-describedby` from them. */
  ids: Record<StudentDraftField, string>;
  /** The error to show for a field, or `undefined` — the owner decides *when* an error is shown. */
  errorFor: (field: ValidatedStudentField) => string | undefined;
  /** The user has left this field at least once. */
  onBlur: (field: ValidatedStudentField) => void;
  /**
   * The ADR-001 D-2 derived triple as the server last reported it, or `undefined` on a create.
   *
   * Taken as three loose values rather than as the `Student` they came from, because that is all this
   * component may know about them: a component holding the whole student is one refactor away from
   * being asked to put one of these in the draft.
   */
  derived: { course?: string; yearLevel?: string; section?: string } | undefined;
  /** Labels the read-only panel as a region, so it is announced with a name rather than as a blank group. */
  derivedHeadingId: string;
}

export function StudentFormFields({
  draft,
  onChange,
  ids,
  errorFor,
  onBlur,
  derived,
  derivedHeadingId,
}: StudentFormFieldsProps) {
  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField
        id={ids.studentNumber}
        label="Student number"
        value={draft.studentNumber}
        onChange={(e) => onChange({ studentNumber: e.target.value })}
        onBlur={() => onBlur("studentNumber")}
        error={errorFor("studentNumber") !== undefined}
        helperText={
          errorFor("studentNumber") ??
          `Required. Stored exactly as typed — the registrar's value, not normalised. Up to ${STUDENT_NUMBER_MAX_LENGTH} characters.`
        }
        required
        // The dialog's first focusable control, so opening it lands on the thing to type in.
        autoFocus
        // `off` throughout this form, and it is a decision rather than a default. Browser autofill is
        // built to fill in *the person at the keyboard*, and every box here belongs to a third party:
        // an admin entering a student would be offered their own name, e-mail and address.
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.firstName}
        label="First name"
        value={draft.firstName}
        onChange={(e) => onChange({ firstName: e.target.value })}
        onBlur={() => onBlur("firstName")}
        error={errorFor("firstName") !== undefined}
        helperText={errorFor("firstName") ?? `Required. Up to ${PERSON_NAME_MAX_LENGTH} characters.`}
        required
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.middleName}
        label="Middle name"
        value={draft.middleName}
        onChange={(e) => onChange({ middleName: e.target.value })}
        onBlur={() => onBlur("middleName")}
        error={errorFor("middleName") !== undefined}
        // The dash is said out loud because the rule is otherwise invisible: `cleanName` folds a
        // placeholder to nothing, so typing "-" and saving leaves the field empty. A user who was not
        // told would read that as the form having lost what they typed.
        helperText={
          errorFor("middleName") ??
          `Optional. A dash and a blank both mean “no middle name”. Up to ${PERSON_NAME_MAX_LENGTH} characters.`
        }
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.lastName}
        label="Last name"
        value={draft.lastName}
        onChange={(e) => onChange({ lastName: e.target.value })}
        onBlur={() => onBlur("lastName")}
        error={errorFor("lastName") !== undefined}
        helperText={errorFor("lastName") ?? `Required. Up to ${PERSON_NAME_MAX_LENGTH} characters.`}
        required
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.email}
        label="Email"
        // `type="email"` for the keyboard it brings up on a touch device and nothing else: the form is
        // `noValidate`, so the browser's own format rule never fires. That is deliberate — the server
        // checks the length of this column and no format at all, so a client-side format rule would
        // refuse an address the API would have accepted.
        type="email"
        value={draft.email}
        onChange={(e) => onChange({ email: e.target.value })}
        onBlur={() => onBlur("email")}
        error={errorFor("email") !== undefined}
        helperText={
          errorFor("email") ??
          `Optional. Stored in lower case. Up to ${EMAIL_MAX_LENGTH} characters.`
        }
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.gender}
        label="Gender"
        value={draft.gender}
        onChange={(e) => onChange({ gender: e.target.value })}
        onBlur={() => onBlur("gender")}
        error={errorFor("gender") !== undefined}
        // Free text rather than a picker, matching the column: §4.3 types this `nvarchar(20)` with no
        // value set behind it, so a picker here would invent a closed set the server does not have and
        // would refuse whatever the roster import wrote.
        helperText={
          errorFor("gender") ?? `Optional. Free text. Up to ${GENDER_MAX_LENGTH} characters.`
        }
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.photoUrl}
        label="Photo URL"
        value={draft.photoUrl}
        onChange={(e) => onChange({ photoUrl: e.target.value })}
        onBlur={() => onBlur("photoUrl")}
        error={errorFor("photoUrl") !== undefined}
        helperText={
          errorFor("photoUrl") ??
          `Optional. A link to the student's photo. Up to ${PHOTO_URL_MAX_LENGTH} characters.`
        }
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.status}
        label="Status"
        select
        value={draft.status}
        // Narrowed against the same set the options are built from rather than asserted back to the
        // union — the same reasoning `EventFormFields` records for the attendance mode. The lookup
        // cannot miss for a value that came from these options, and if it somehow did it leaves the
        // current status standing rather than putting one in the draft the server would refuse.
        onChange={(e) => {
          const chosen = knownStatus(e.target.value);
          if (chosen !== undefined) onChange({ status: chosen });
        }}
        // A real control, unlike the event form's absent status field: §4.3 makes this an ordinary
        // column the write surface owns, where an event's status is a state machine with one door.
        helperText="A new student is Active unless you choose otherwise."
        fullWidth
      >
        {STUDENT_STATUSES.map((status) => (
          <MenuItem key={status} value={status}>
            {STUDENT_STATUS_LABELS[status]}
          </MenuItem>
        ))}
      </TextField>

      {/* Not disabled inputs. A disabled `TextField` is skipped by the Tab order and read as an
          unavailable control, which frames these as fields the user might one day be allowed to
          edit — they are not fields at all. Plain labelled text sits in the reading order, is
          announced with its label, and cannot be mistaken for something that greys out. */}
      <Box
        component="section"
        aria-labelledby={derivedHeadingId}
        sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}
      >
        <Typography id={derivedHeadingId} variant="subtitle2" component="h3">
          From the academic records (read-only)
        </Typography>

        {derived !== undefined && (
          <Box
            component="dl"
            sx={{
              display: "grid",
              gridTemplateColumns: "auto 1fr",
              columnGap: 2,
              rowGap: 0.5,
              my: 1.5,
            }}
          >
            <Typography component="dt" variant="body2" color="text.secondary">
              Course
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {derived.course ?? NO_VALUE}
            </Typography>

            <Typography component="dt" variant="body2" color="text.secondary">
              Year level
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {derived.yearLevel ?? NO_VALUE}
            </Typography>

            <Typography component="dt" variant="body2" color="text.secondary">
              Section
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {derived.section ?? NO_VALUE}
            </Typography>
          </Box>
        )}

        <Typography variant="body2" color="text.secondary" sx={{ mt: derived === undefined ? 1 : 0 }}>
          {derived === undefined ? DERIVED_NOTE_NEW : DERIVED_NOTE}
        </Typography>
      </Box>
    </Stack>
  );
}
