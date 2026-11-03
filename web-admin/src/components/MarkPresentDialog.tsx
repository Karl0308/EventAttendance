// "Mark present by hand" (#706 F2-2, JJ ruling W3): one picker that searches students AND personnel, the type
// shown on every option, and one write — `POST /attendance/manual` with exactly one id.
//
// The write is `EventDetail.tsx`'s, as everywhere in this slice (a dialog that owns its write ends it on
// unmount); this component owns only the picker's reads and the choice.
//
// Two sources, loaded differently on purpose:
//   - Students are searched on the server (`GET /students?search=`), because a roster can be tens of
//     thousands of rows and `listStudents()` refuses past its ceiling. Nothing is requested until the
//     operator has typed `MIN_SEARCH_CHARS` characters, and the typing is debounced.
//   - Personnel come from the event picker route once and are matched here; a school's staff list is small.
// Each source fails on its own: a role that may not read students can still pick personnel, and the other
// way round. The failure is said under the picker rather than swallowed.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  TextField,
  Typography,
} from "@mui/material";
import { api, describeApiError } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import {
  attendeeFromPersonnel,
  attendeeFromStudent,
  attendeeKey,
  attendeeLabel,
  manualRefusalText,
  manualRequestFor,
  matchesPersonnel,
  MIN_SEARCH_CHARS,
} from "../manualAttendance";
import type { ManualAttendee } from "../manualAttendance";
import { PERSON_TYPE_LABEL } from "../attendanceRows";
import { useApiResource } from "../useApiResource";
import { useDebounced } from "../useDebounced";
import type { ManualAttendanceRequest } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const TITLE_ID = "mark-present-title";
const PICKER_ID = "mark-present-picker";
const SEARCH_SETTLE_MS = 300;
/** Enough to find a person by typing a little more of their name; the operator narrows by typing, not paging. */
const STUDENT_RESULTS = 20;
const NO_ATTENDEES: readonly ManualAttendee[] = [];

const HEADING_NOT_MARKED = "Not marked present";
const HEADING_MAYBE_MARKED = "This may have been recorded";
const RESEND_WITHHELD =
  "Mark present is disabled because pressing it again could record the same person twice. Close this dialog " +
  "and check the live attendance list before trying again.";

interface Props {
  eventId: string;
  eventName: string;
  onClose: () => void;
  onSubmit: (request: ManualAttendanceRequest, attendee: ManualAttendee) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function MarkPresentDialog({ eventId, eventName, onClose, onSubmit, running, failure }: Props) {
  const [picked, setPicked] = useState<ManualAttendee | null>(null);
  const [text, setText] = useState("");
  const [submitAttempted, setSubmitAttempted] = useState(false);
  const query = useDebounced(text.trim(), SEARCH_SETTLE_MS);

  const students = useApiResource<readonly ManualAttendee[]>(
    () =>
      query.length < MIN_SEARCH_CHARS
        ? Promise.resolve(NO_ATTENDEES)
        : api
            .listStudentsPage({ search: query }, 1, STUDENT_RESULTS)
            .then((page) => page.students.map(attendeeFromStudent)),
    [query],
  );
  const personnel = useApiResource<readonly ManualAttendee[]>(
    () => api.listPersonnelPicker().then((entries) => entries.map(attendeeFromPersonnel)),
    [],
  );

  const options: ManualAttendee[] = [
    ...(students.status === "ready" ? students.data : NO_ATTENDEES),
    ...(personnel.status === "ready" ? personnel.data.filter((p) => matchesPersonnel(p, text)) : NO_ATTENDEES),
  ];
  // Keep the chosen option in the list even after the typing that found it changes, or MUI warns that
  // the value is not among the options and the field blanks.
  const withPicked = picked !== null && !options.some((o) => attendeeKey(o) === attendeeKey(picked))
    ? [picked, ...options]
    : options;

  const searching = students.status === "loading" || (students.status === "ready" && students.refreshing);
  const refusal = failure === undefined ? undefined : manualRefusalText(failure.error);
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);
  const noChoice = submitAttempted && picked === null;

  const sourceNotes: string[] = [];
  if (students.status === "error") sourceNotes.push(`Students could not be searched (${describeApiError(students.error)}).`);
  if (personnel.status === "error") sourceNotes.push(`Personnel could not be loaded (${describeApiError(personnel.error)}).`);

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSubmitAttempted(true);
    if (picked === null) {
      document.getElementById(PICKER_ID)?.focus();
      return;
    }
    onSubmit(manualRequestFor(eventId, picked), picked);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={TITLE_ID}>Mark present by hand</DialogTitle>
        <DialogContent>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Records one student or personnel member as Present at “{eventName}” without a card tap. It is
            kept as a manual entry.
          </Typography>

          {failure !== undefined &&
            (refusal !== undefined ? (
              <Alert severity="error" role="alert" sx={{ mb: 2 }}>
                <Typography variant="subtitle2">{HEADING_NOT_MARKED}</Typography>
                <Typography variant="body2">{refusal}</Typography>
              </Alert>
            ) : (
              <WriteFailureAlert
                error={failure.error}
                notApplied={HEADING_NOT_MARKED}
                mayHaveApplied={HEADING_MAYBE_MARKED}
                resendWithheld={RESEND_WITHHELD}
              />
            ))}

          <Autocomplete<ManualAttendee>
            fullWidth
            autoHighlight
            options={withPicked}
            value={picked}
            onChange={(_event, next) => setPicked(next)}
            inputValue={text}
            onInputChange={(_event, next, reason) => {
              // `reset` fires when a value is picked and writes its label into the box; keeping that
              // would turn the next search into a search for the label. Only typing updates the query.
              if (reason === "input" || reason === "clear") setText(next);
            }}
            // Filtering is the server's (students) and `matchesPersonnel`'s; MUI's own would hide a
            // student found by card serial or by a name part the label does not show first.
            filterOptions={(all) => all}
            getOptionKey={attendeeKey}
            getOptionLabel={attendeeLabel}
            isOptionEqualToValue={(a, b) => attendeeKey(a) === attendeeKey(b)}
            loading={searching}
            loadingText="Searching…"
            noOptionsText={
              text.trim().length < MIN_SEARCH_CHARS
                ? `Type at least ${MIN_SEARCH_CHARS} characters of a name or ID number.`
                : "Nobody matches that."
            }
            renderOption={(props, option) => {
              const { key, ...rest } = props;
              return (
                <li key={key} {...rest}>
                  <Box sx={{ display: "flex", alignItems: "center", gap: 1, width: "100%" }}>
                    <Box sx={{ flexGrow: 1 }}>
                      <Typography>{option.name}</Typography>
                      <Typography variant="body2" color="text.secondary">
                        {option.number}
                      </Typography>
                    </Box>
                    <Chip size="small" variant="outlined" label={PERSON_TYPE_LABEL[option.type]} />
                  </Box>
                </li>
              );
            }}
            renderInput={(params) => (
              <TextField
                {...params}
                id={PICKER_ID}
                label="Student or personnel member"
                placeholder="Search by name or ID number"
                autoFocus
                required
                error={noChoice}
                helperText={noChoice ? "Pick one person from the list." : "Students and personnel are searched together."}
                slotProps={{
                  input: {
                    ...params.InputProps,
                    endAdornment: (
                      <>
                        {searching ? <CircularProgress size={16} aria-hidden /> : null}
                        {params.InputProps.endAdornment}
                      </>
                    ),
                  },
                }}
              />
            )}
          />

          {sourceNotes.length > 0 && (
            <Alert severity="warning" role="status" sx={{ mt: 2 }}>
              {sourceNotes.map((note) => (
                <Typography key={note} variant="body2">
                  {note}
                </Typography>
              ))}
            </Alert>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : "Mark present"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
