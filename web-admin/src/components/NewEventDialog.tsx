// The create-event form: the draft, its rules, and the failure the page hands back. The write itself
// is NOT here — `Events.tsx` owns it (see `NewEventDialogProps`).
//
// A `Dialog` rather than a route of its own. Creating an event is a short interruption of the list —
// the user is looking at the events, decides to add one, and wants to be back at the list with the
// new row on it. A `/events/new` route would give that interruption a URL, browser history and a
// back button, and every one of those is a way to lose a half-typed form: Back mid-draft discards it
// with no warning, and a deep link to `/events/new` is a URL nobody has a reason to share. The
// routing table stays four lines long, which is the other half of the argument.
//
// It lives in `components/` rather than `pages/` because `pages/` is one file per route in
// `App.tsx`, and this is not one.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";
import { ATTENDANCE_MODES } from "../types";
import type { AttendanceMode, EventWriteRequest } from "../types";

// ---------------------------------------------------------------------------------------------
// The server's rules, restated
// ---------------------------------------------------------------------------------------------
//
// Every limit below is `EventText` in `EAMS.Domain/DomainValues.cs`, checked again here. Not because
// the client is trusted — it is not, and `EventService.Validate` still has the last word — but
// because a round trip to be told a name is 4 characters too long is a bad way to learn it, and the
// server's answer arrives after the user has stopped looking at the field.
//
// Named, because a `200` sitting in a `maxLength` prop is a number nobody can check against
// anything. Restated rather than fetched: there is no endpoint that publishes them, so the honest
// description of these is "a copy that will drift if §4.5 changes" — which is what the citation
// above is for.

const NAME_MAX_LENGTH = 200;
const DESCRIPTION_MAX_LENGTH = 2000;
const LOCATION_MAX_LENGTH = 300;
const MIN_GRACE_MINUTES = 0;
const MAX_GRACE_MINUTES = 1440;

/** §4.5's own column default (`GraceMinutes int NOT NULL, default 0`), not a suggestion of ours. */
const DEFAULT_GRACE_MINUTES = "0";

/** §4.5's `AttendanceMode` default, which the server also applies when the field is absent. */
const DEFAULT_ATTENDANCE_MODE: AttendanceMode = "Single";

/** What the mode picker calls each value. The values themselves are the wire's, and are not changed. */
const ATTENDANCE_MODE_LABELS: Record<AttendanceMode, string> = {
  Single: "Single — one tap records attendance",
  TimeInOut: "Time in / time out — a tap to arrive and a tap to leave",
};

// ---------------------------------------------------------------------------------------------
// The draft
// ---------------------------------------------------------------------------------------------

/**
 * What is in the boxes, which is not what is sent.
 *
 * Every field is the control's own value type — text for the numbers and the datetimes included.
 * Holding `graceMinutes` as a `number` would mean the box could not be empty while the user retypes
 * it, and holding the datetimes as `Date`s would mean a half-typed one had no representation at all.
 * The conversion to `EventWriteRequest` happens once, in `validate`, and only when it can succeed.
 */
interface Draft {
  name: string;
  description: string;
  location: string;
  /** `datetime-local` — a wall-clock reading with no zone. `instantFrom` gives it one. */
  startAt: string;
  endAt: string;
  attendanceMode: AttendanceMode;
  graceMinutes: string;
  requireRegistration: boolean;
}

const EMPTY_DRAFT: Draft = {
  name: "",
  description: "",
  location: "",
  startAt: "",
  endAt: "",
  attendanceMode: DEFAULT_ATTENDANCE_MODE,
  graceMinutes: DEFAULT_GRACE_MINUTES,
  requireRegistration: false,
};

/**
 * The fields that can carry an error, **in the order they appear on screen** — which is what makes
 * "focus the first invalid one" land on the first invalid one the user can see rather than the first
 * this file happens to list. `attendanceMode` and `requireRegistration` are absent because a select
 * and a checkbox cannot hold a value outside their own options.
 */
const VALIDATED_FIELDS = [
  "name",
  "startAt",
  "endAt",
  "location",
  "graceMinutes",
  "description",
] as const;

type ValidatedField = (typeof VALIDATED_FIELDS)[number];

type FieldErrors = Partial<Record<ValidatedField, string>>;

const NO_ERRORS: FieldErrors = {};

/**
 * Stable ids, because MUI derives the `<label for>` and the `aria-describedby` that ties an input to
 * its error text from the `id` given to the `TextField`. Generated ids would work for the label and
 * would make the focus-the-first-invalid lookup below impossible to write.
 */
const FIELD_ID = {
  name: "new-event-name",
  description: "new-event-description",
  location: "new-event-location",
  startAt: "new-event-start-at",
  endAt: "new-event-end-at",
  attendanceMode: "new-event-attendance-mode",
  graceMinutes: "new-event-grace-minutes",
  requireRegistration: "new-event-require-registration",
} as const satisfies Record<keyof Draft, string>;

const DIALOG_TITLE_ID = "new-event-dialog-title";

// ---------------------------------------------------------------------------------------------
// What the failure alert says
// ---------------------------------------------------------------------------------------------
//
// The heading is the largest, first-read line in the alert, and it used to assert one thing
// unconditionally: "The event was not created". For two of the failures this form can produce that is
// false, and it sat two lines above the sentence saying so — the `malformed` raised by `createEvent`'s
// 201 mapping, whose body reads "The event *was* created…", and a network failure or timeout on the
// way back, whose body reads "may or may not have been carried out". A user who reads the heading and
// stops has been told exactly what that rename exists to prevent them believing.
//
// So the heading is derived from `advise().serverEffect` — from what the seam actually knows about
// what the server did — rather than restated. Deleting it was not an option: `role="alert"` needs a
// lead sentence, and an alert whose first line is a stack-shaped detail is worse than one whose first
// line is wrong.

/** The server decided and wrote nothing: a §4.5 validation refusal, a 409 conflict. */
const HEADING_NOT_CREATED = "The event was not created";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_CREATED = "The event may have been created";

/**
 * Shown whenever Create is disabled by the failure rather than by the request in flight.
 *
 * There is deliberately no one-click resend for `may-duplicate`. A button that may create a second
 * event is the defect, not the remedy, and `POST /events` has no idempotency key that would make the
 * second press a no-op. What the dialog offers instead is the truth and the way out: the list behind
 * has already been re-read, so closing this is the action that answers the question.
 *
 * Said out loud because the file's own rule is that a control which greys out without saying why
 * leaves the user hunting.
 */
const RESEND_WITHHELD =
  "Create is disabled because pressing it again could create a second event. Close this dialog and " +
  "look for the event in the list — it is re-read after every failed attempt — before trying again.";

// ---------------------------------------------------------------------------------------------
// Parsing and validation
// ---------------------------------------------------------------------------------------------

/**
 * A `datetime-local` reading as the instant it names **in this browser's zone**.
 *
 * The conversion has to happen here. `"2026-08-01T09:00"` carries no offset, so sending it raw would
 * leave the zone to the server's parser, and the server runs in UTC while this school is UTC+8 —
 * the same eight-hour misjudgement `UtcTime` records on the backend, except silent, because an event
 * eight hours out is still a perfectly valid event. `new Date(local)` reads a bare date-time as
 * local time, which is what the user typed; `toISOString()` then names the instant unambiguously.
 *
 * Epoch milliseconds rather than a `Date`, so the start/end comparison below is `<=` on two numbers
 * instead of a wrong-looking comparison of two objects.
 */
function instantFrom(local: string): number | undefined {
  if (local === "") return undefined;
  const ms = new Date(local).getTime();
  return Number.isNaN(ms) ? undefined : ms;
}

/** A whole number of minutes inside §4.5's range, or nothing. `Number("")` is 0, hence the guard. */
function wholeMinutesFrom(raw: string): number | undefined {
  const text = raw.trim();
  if (text === "") return undefined;
  const value = Number(text);
  if (!Number.isInteger(value)) return undefined;
  return value >= MIN_GRACE_MINUTES && value <= MAX_GRACE_MINUTES ? value : undefined;
}

/**
 * Either the request, or why there is not one. One function, so the answer to "may this be sent" and
 * the answer to "what exactly is sent" cannot disagree — a separate `isValid` predicate beside a
 * separate `toRequest` builder is two readings of one set of rules, and the day they drift is the day
 * a form submits a body it just told the user was fine.
 */
type Validated =
  | { ok: true; request: EventWriteRequest }
  | { ok: false; errors: FieldErrors };

function validate(draft: Draft): Validated {
  const errors: FieldErrors = {};

  // Trimmed before measuring, because the server trims before measuring (`EventText.IsValidName`,
  // `Apply`). Measuring the untrimmed string would refuse a name the server would have accepted.
  const name = draft.name.trim();
  if (name.length === 0) {
    errors.name = "A name is required.";
  } else if (name.length > NAME_MAX_LENGTH) {
    errors.name = `${NAME_MAX_LENGTH} characters at most; this is ${name.length}.`;
  }

  const description = draft.description.trim();
  if (description.length > DESCRIPTION_MAX_LENGTH) {
    errors.description = `${DESCRIPTION_MAX_LENGTH} characters at most; this is ${description.length}.`;
  }

  const location = draft.location.trim();
  if (location.length > LOCATION_MAX_LENGTH) {
    errors.location = `${LOCATION_MAX_LENGTH} characters at most; this is ${location.length}.`;
  }

  const startAt = instantFrom(draft.startAt);
  if (startAt === undefined) errors.startAt = "A start date and time is required.";

  const endAt = instantFrom(draft.endAt);
  if (endAt === undefined) {
    errors.endAt = "An end date and time is required.";
  } else if (startAt !== undefined && endAt <= startAt) {
    // The server's rule verbatim, and it is `<=` there too: an event whose window is empty or
    // inverted can never be attended, because the tap path decides Present versus Late from the
    // start and the grace period.
    errors.endAt = "The end must be after the start.";
  }

  const graceMinutes = wholeMinutesFrom(draft.graceMinutes);
  if (graceMinutes === undefined) {
    errors.graceMinutes = `A whole number of minutes from ${MIN_GRACE_MINUTES} to ${MAX_GRACE_MINUTES}.`;
  }

  // The three `=== undefined` arms are what narrow the parsed values for the request below; they
  // cannot fire on their own, because each of them set an error above. The `errors` check is the
  // real condition and it is first.
  if (
    Object.keys(errors).length > 0 ||
    startAt === undefined ||
    endAt === undefined ||
    graceMinutes === undefined
  ) {
    return { ok: false, errors };
  }

  return {
    ok: true,
    request: {
      name,
      // `null`, not `""`. An empty box means the organizer did not give a location; an empty string
      // is a location, and the column would store it as one — after which "has a location" is true
      // for an event that has none.
      description: description === "" ? null : description,
      location: location === "" ? null : location,
      startAt: new Date(startAt).toISOString(),
      endAt: new Date(endAt).toISOString(),
      attendanceMode: draft.attendanceMode,
      graceMinutes,
      requireRegistration: draft.requireRegistration,
    },
  };
}

// ---------------------------------------------------------------------------------------------
// The dialog
// ---------------------------------------------------------------------------------------------

/**
 * The dialog owns the draft and its rules. It does not own the write.
 *
 * `useApiMutation` used to live inside this component, which made the dialog's own unmount the end of
 * the request: Cancel, Escape and the backdrop had to be blocked for as long as the create ran — up
 * to `REQUEST_TIMEOUT_MS`, fifteen seconds — or the outcome went to `console.debug` and the list was
 * never re-read. That lock did not close the hole it was built for (browser Back, a deep link or any
 * route change unmounted the dialog anyway), and what it did reliably buy was fifteen seconds of a
 * keyboard user held inside a focus-trapped dialog with no way out.
 *
 * With the write one level up, this component is free to unmount at any moment and every dismissal
 * works unconditionally. It is also the shape D3 needs: a four-step import wizard cannot ship behind
 * a modal lock.
 */
interface NewEventDialogProps {
  /** Dismiss without creating: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /**
   * Send this draft. Returns nothing on purpose — the page owns the mutation, so the outcome is the
   * page's to route, and a promise returned here is one a submit handler would drop.
   */
  onSubmit: (request: EventWriteRequest) => void;
  /** The page's create is in flight. */
  running: boolean;
  /**
   * How the last create failed, or `undefined` for "no failure to show".
   *
   * Wrapped in an object rather than passed as a bare `unknown`, because `unknown` includes
   * `undefined`: "failed, with an undefined reason" and "has not failed" would be the same value, and
   * collapsing those two is precisely what `useApiResource` and `useApiMutation` exist to forbid.
   */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `Events.tsx` — so every open starts from a fresh `EMPTY_DRAFT`. */
export default function NewEventDialog({
  onClose,
  onSubmit,
  running,
  failure,
}: NewEventDialogProps) {
  const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT);
  // A set, not a map of booleans: "has been left at least once" is membership, and there is no third
  // state for it to be in.
  const [touched, setTouched] = useState<ReadonlySet<ValidatedField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validate(draft);
  const errors: FieldErrors = checked.ok ? NO_ERRORS : checked.errors;

  /**
   * Errors are computed on every keystroke and *shown* only once the user has left the field or
   * pressed Create. Showing them as they type turns "Charity R" into a red box complaining a name is
   * required, and a form that shouts before the user has finished a word is a form people learn to
   * ignore.
   */
  const errorFor = (field: ValidatedField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedField) =>
    setTouched((current) => new Set(current).add(field));

  /**
   * The failure and everything the alert says about it, settled together — so the JSX tests one thing
   * and the heading, the guidance sentence and the state of the Create button cannot disagree about
   * which failure they are describing.
   */
  const failed =
    failure === undefined ? undefined : { error: failure.error, guidance: advise(failure.error) };

  /**
   * Whether pressing Create again is a thing this dialog is willing to offer.
   *
   * `!== "safe"` rather than the old `!retryable`: `advise().retryable` is three-valued now, and the
   * arm that was missing is the one that mattered. A connection reset on the way back used to read as
   * plainly retryable, so the button stayed live and the guidance said "then retry" — on a write that
   * may already have created the event. `"may-duplicate"` is truthy, so the negation would have gone
   * on offering it.
   */
  const resendUnsafe = failed !== undefined && failed.guidance.retryable !== "safe";

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    // Re-validated here rather than reusing `checked` from render: they agree today, and relying on
    // that is relying on this handler never being called from a closure one render behind.
    const now = validate(draft);
    if (!now.ok) {
      // Focus follows the refusal. The Create button is at the bottom of the dialog, so a keyboard
      // user who is told "check the fields" and left standing on that button has to Shift+Tab past
      // everything to reach the problem; a screen-reader user is told nothing at all, because the
      // field errors appear above where they are standing (WCAG 3.3.1). Landing on the input makes
      // its label, its value and its now-associated error the next thing announced.
      const firstInvalid = VALIDATED_FIELDS.find((field) => now.errors[field] !== undefined);
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

          `noValidate` because `required` and the number input's min/max are here to be *announced* —
          `aria-required`, and a hint for the spinner — not to be enforced by the browser. Left on,
          Chrome would block submit with a transient native bubble that no screen reader reads
          reliably and that says "Please fill out this field" where `validate` says which field and
          which limit. One set of rules, in one place, mirroring the server's. */}
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>New event</DialogTitle>

        <DialogContent>
          {/* Said once, plainly, instead of offering a status picker that would do nothing. The
              server creates every event as a Draft and `PATCH /events/{id}/status` is the only way
              out of it — which is what makes the roster freeze on close impossible to skip. */}
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            New events are created as <strong>Draft</strong>. Opening one for attendance is a
            separate step.
          </Typography>

          {failed !== undefined && (
            // `role="alert"` so the refusal is announced rather than merely painted. It is not given
            // focus, unlike `ErrorState`: the Create button that raised this is still mounted and
            // still under the user's finger, and moving them away from it would cost them their
            // place for no gain.
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              {/* Derived, never asserted — see the constants above. `"unknown"` is the write that
                  went out and may have been applied; `"none"` is the server having decided. */}
              <AlertTitle>
                {failed.guidance.serverEffect === "unknown"
                  ? HEADING_MAYBE_CREATED
                  : HEADING_NOT_CREATED}
              </AlertTitle>
              {/* Carries the server's own sentence. A 400 from this endpoint names the field and the
                  limit in `detail`, and `httpError` ranks `detail` above both the fixed `title` and
                  the machine `code` so it survives to here instead of arriving as "The request could
                  not be processed." or as a bare outcome token. */}
              <Typography variant="body2">{describeApiError(failed.error)}</Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {failed.guidance.message}
              </Typography>
              {resendUnsafe && (
                <Typography variant="body2" sx={{ mt: 1 }}>
                  {RESEND_WITHHELD}
                </Typography>
              )}
            </Alert>
          )}

          <Stack spacing={2} sx={{ mt: 1 }}>
            <TextField
              id={FIELD_ID.name}
              label="Name"
              value={draft.name}
              onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))}
              onBlur={() => markTouched("name")}
              error={errorFor("name") !== undefined}
              helperText={errorFor("name") ?? `Required. Up to ${NAME_MAX_LENGTH} characters.`}
              required
              // The dialog's first focusable control, so opening it lands on the thing to type in.
              autoFocus
              fullWidth
            />

            <TextField
              id={FIELD_ID.startAt}
              label="Starts"
              type="datetime-local"
              value={draft.startAt}
              onChange={(e) => setDraft((d) => ({ ...d, startAt: e.target.value }))}
              onBlur={() => markTouched("startAt")}
              error={errorFor("startAt") !== undefined}
              helperText={errorFor("startAt") ?? "Your local time."}
              required
              // A datetime input always shows its placeholder text, so the label has nowhere to sit
              // unless it is shrunk from the start.
              slotProps={{ inputLabel: { shrink: true } }}
              fullWidth
            />

            <TextField
              id={FIELD_ID.endAt}
              label="Ends"
              type="datetime-local"
              value={draft.endAt}
              onChange={(e) => setDraft((d) => ({ ...d, endAt: e.target.value }))}
              onBlur={() => markTouched("endAt")}
              error={errorFor("endAt") !== undefined}
              helperText={errorFor("endAt") ?? "Must be after the start."}
              required
              slotProps={{ inputLabel: { shrink: true } }}
              fullWidth
            />

            <TextField
              id={FIELD_ID.location}
              label="Location"
              value={draft.location}
              onChange={(e) => setDraft((d) => ({ ...d, location: e.target.value }))}
              onBlur={() => markTouched("location")}
              error={errorFor("location") !== undefined}
              helperText={errorFor("location") ?? `Optional. Up to ${LOCATION_MAX_LENGTH} characters.`}
              fullWidth
            />

            <TextField
              id={FIELD_ID.attendanceMode}
              label="Attendance mode"
              select
              value={draft.attendanceMode}
              // Narrowed against the same array the options are built from, rather than asserted
              // back to the union. The assertion would compile and would be a lie the moment anyone
              // rendered a ninth `MenuItem` by hand; the lookup cannot miss for a value that came
              // from these options, and if it somehow did it leaves the current mode standing rather
              // than putting a value in the draft that the server is guaranteed to refuse.
              onChange={(e) => {
                const chosen = ATTENDANCE_MODES.find((mode) => mode === e.target.value);
                if (chosen !== undefined) setDraft((d) => ({ ...d, attendanceMode: chosen }));
              }}
              fullWidth
            >
              {ATTENDANCE_MODES.map((mode) => (
                <MenuItem key={mode} value={mode}>
                  {ATTENDANCE_MODE_LABELS[mode]}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              id={FIELD_ID.graceMinutes}
              label="Grace period (minutes)"
              type="number"
              value={draft.graceMinutes}
              onChange={(e) => setDraft((d) => ({ ...d, graceMinutes: e.target.value }))}
              onBlur={() => markTouched("graceMinutes")}
              error={errorFor("graceMinutes") !== undefined}
              helperText={
                errorFor("graceMinutes") ??
                `Arrivals within this many minutes of the start count as Present. ${MIN_GRACE_MINUTES}–${MAX_GRACE_MINUTES}.`
              }
              // Advisory only — a number input can still be typed into out of range, and `validate`
              // is what actually refuses it. These give the spinner sensible steps and the browser
              // something to hint with.
              slotProps={{
                htmlInput: { min: MIN_GRACE_MINUTES, max: MAX_GRACE_MINUTES, step: 1 },
              }}
              fullWidth
            />

            <TextField
              id={FIELD_ID.description}
              label="Description"
              value={draft.description}
              onChange={(e) => setDraft((d) => ({ ...d, description: e.target.value }))}
              onBlur={() => markTouched("description")}
              error={errorFor("description") !== undefined}
              helperText={
                errorFor("description") ?? `Optional. Up to ${DESCRIPTION_MAX_LENGTH} characters.`
              }
              multiline
              minRows={2}
              fullWidth
            />

            <FormControlLabel
              control={
                <Checkbox
                  id={FIELD_ID.requireRegistration}
                  checked={draft.requireRegistration}
                  onChange={(e) =>
                    setDraft((d) => ({ ...d, requireRegistration: e.target.checked }))
                  }
                />
              }
              label="Require registration"
            />
          </Stack>
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
            // Deliberately NOT disabled for a form with errors in it: a button that greys out
            // without saying why leaves the user hunting, where a press that answers "this field,
            // this limit" and puts the cursor in it tells them exactly what to fix. Where it *is*
            // disabled by a failure, `RESEND_WITHHELD` above says why and what to do instead.
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Creating…" : "Create event"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
