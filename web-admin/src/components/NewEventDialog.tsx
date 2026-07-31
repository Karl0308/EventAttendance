// The create-event form: an empty draft, the shared rules, and the failure the page hands back. The
// write itself is NOT here — `Events.tsx` owns it (see `NewEventDialogProps`).
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
//
// What used to be here and is not any more: the §4.5 limits, the draft type, `instantFrom`,
// `validate` and the eight controls. They are shared with `EditEventDialog` now — `eventDraft.ts` and
// `EventFormFields.tsx` — because `POST /events` and `PUT /events/{id}` take the same body and are
// checked by the same server code, and a second copy of that reading would only ever drift.

import { useState } from "react";
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
import { EMPTY_DRAFT, NO_ERRORS, VALIDATED_FIELDS, fieldIdsFor, lockedFor, validate } from "../eventDraft";
import type { Draft, FieldErrors, ValidatedField } from "../eventDraft";
import type { EventWriteRequest } from "../types";
import { EventFormFields } from "./EventFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = fieldIdsFor("new-event");
const DIALOG_TITLE_ID = "new-event-dialog-title";

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
 * Said out loud because the standing rule is that a control which greys out without saying why leaves
 * the user hunting.
 */
const RESEND_WITHHELD =
  "Create is disabled because pressing it again could create a second event. Close this dialog and " +
  "look for the event in the list — it is re-read after every failed attempt — before trying again.";

/** Nothing is locked on a create: there is no event yet whose state could forbid a field. */
// Nothing is locked on a create — every field is being set for the first time, so there is no
// existing attendance row whose meaning an edit could rewrite. Read from `lockedFor` rather than
// declared again here, so the two forms cannot drift on what "unlocked" means.
const NOTHING_LOCKED = lockedFor("everything");

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
   * Whether pressing Create again is a thing this dialog is willing to offer. `isResendUnsafe` is
   * `advise().retryable !== "safe"` — never a negation, because `"may-duplicate"` is truthy and a
   * negation would go on offering the button that creates the second event.
   */
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

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

          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_CREATED}
              mayHaveApplied={HEADING_MAYBE_CREATED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <EventFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            locked={NOTHING_LOCKED}
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
