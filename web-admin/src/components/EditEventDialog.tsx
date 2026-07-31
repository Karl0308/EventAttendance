// The edit-event form: a draft filled from the event, the same rules the create form is held to, and
// the failure the page hands back. The write itself is NOT here — `EventDetail.tsx` owns it, for the
// reason `NewEventDialog` records: a dialog that owns its own write has to block its own dismissal
// for as long as the request runs, and that lock reliably traps a keyboard user without closing the
// hole it was built for.
//
// What it does that the create form does not: it starts from an event, and it renders that event's
// *edit mode*. `PUT /events/{id}` has three, not two — everything, descriptive-only, and nothing —
// and a form that ignored the middle one would produce 409s naming fields the user never touched.

import { useState } from "react";
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
import { isResendUnsafe } from "../apiGuidance";
import {
  NO_ERRORS,
  VALIDATED_FIELDS,
  draftFrom,
  fieldIdsFor,
  lockedFor,
  validate,
} from "../eventDraft";
import type { Draft, EditScope, FieldErrors, ValidatedField } from "../eventDraft";
import type { EventItem, EventWriteRequest } from "../types";
import { EventFormFields } from "./EventFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = fieldIdsFor("edit-event");
const DIALOG_TITLE_ID = "edit-event-dialog-title";

/** The server decided and wrote nothing: a §4.5 validation refusal, a 409 the event's state forbids. */
const HEADING_NOT_SAVED = "The change was not saved";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_SAVED = "The change may have been saved";

/**
 * Shown when Save is disabled by the failure rather than by the request in flight.
 *
 * It does **not** say "pressing it again could save it twice", which would be false: `PUT` is
 * idempotent, so a second send of the same body lands on the same row with the same values. What is
 * true is the thing worth saying — this build cannot tell whether the first one landed, and the way
 * to find out is to look. `advise()` withholds the one-click resend here because `shape` is a proxy
 * for idempotency and is exact only for `POST`; it errs safe, and the sentence stays honest about why.
 */
const RESEND_WITHHELD =
  "Save is disabled because this build cannot tell whether the change was applied. Close this dialog " +
  "and read the event — it is re-read after every failed attempt — before sending it again.";

/**
 * Why the scheduling fields are dead, said in full rather than left to five greyed boxes.
 *
 * The reason is not arbitrary and the user is owed it: an event that was Open before it was cancelled
 * can already hold taps whose Present-versus-Late was decided by its start time and grace period, so
 * moving those now would change what the existing rows mean without changing the rows. The server
 * refuses it for that reason; a form that merely disabled the boxes would leave the user hunting for
 * a permission they do not lack.
 */
const lockedNotice = (status: string) =>
  `This event is ${status}, so its schedule, grace period, attendance mode and registration setting ` +
  "cannot be changed. Attendance may already have been recorded against them — the start time and " +
  "the grace period are what decided Present versus Late for every row — and moving them now would " +
  "change what those rows mean without changing the rows. Its name, description and location can " +
  "still be edited.";

interface EditEventDialogProps {
  /** The event as the page last read it. Snapshotted on open; see `baseline` below. */
  event: EventItem;
  /** How much of it the server will accept a change to. `EventDetail` decides; see `Editability`. */
  scope: EditScope;
  /** Dismiss without saving: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /** Send this body. Returns nothing — the page owns the mutation and routes the outcome. */
  onSubmit: (request: EventWriteRequest) => void;
  /** The page's update is in flight. */
  running: boolean;
  /** How the last save failed, or `undefined`. Wrapped, because `unknown` includes `undefined`. */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `EventDetail.tsx` — so every open starts from the event as it is. */
export default function EditEventDialog({
  event,
  scope,
  onClose,
  onSubmit,
  running,
  failure,
}: EditEventDialogProps) {
  /**
   * The event as it was when this form opened, held rather than read from the prop on every render.
   *
   * The page already hands down a snapshot — `editing` is set once when the dialog opens and is not
   * refreshed by `detail.reload()` — so this holds today whether or not the state exists. It is here
   * to make that independent of how the page chooses to hold it: the page re-reads the event after a
   * failed save, and if that read were ever wired through to this prop, following it would overwrite
   * what the user has typed with what the server currently holds, at exactly the moment they are
   * trying to correct it. Only the initialiser runs, so there is no setter to use and none is
   * destructured.
   */
  const [baseline] = useState<EventItem>(event);
  const [draft, setDraft] = useState<Draft>(() => draftFrom(baseline));
  const [touched, setTouched] = useState<ReadonlySet<ValidatedField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  /**
   * The instants the boxes were filled from, so an untouched box is sent back exactly as it arrived.
   * `datetime-local` has minute resolution and an instant does not — see `DraftOrigin`. On a
   * descriptive-only edit this is what keeps a save the user made to a *name* from being refused for
   * a start time they never went near.
   */
  const origin = { startAt: baseline.startAt, endAt: baseline.endAt };
  const locked = lockedFor(scope);

  const checked = validate(draft, origin);
  const errors: FieldErrors = checked.ok ? NO_ERRORS : checked.errors;

  const errorFor = (field: ValidatedField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedField) =>
    setTouched((current) => new Set(current).add(field));

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validate(draft, origin);
    if (!now.ok) {
      // Focus follows the refusal, for the reason `NewEventDialog` records (WCAG 3.3.1). A locked
      // field can never be the target: `EventDetail` only opens this form for an event whose locked
      // values round-trip, so every field that can carry an error is one the user can reach.
      const firstInvalid = VALIDATED_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Edit event</DialogTitle>

        <DialogContent>
          {/* Said whether or not anything is locked: the absence of a status control is a thing the
              user can otherwise only discover by looking for one. */}
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            This event is <strong>{baseline.status}</strong>. Opening, closing or cancelling it is a
            separate step — it is not part of this form.
          </Typography>

          {scope === "descriptive" && (
            // `role="status"`, not `alert`: it describes the form the user just opened rather than
            // something that went wrong, and it is on screen before they touch anything.
            <Alert severity="info" role="status" sx={{ mb: 2 }}>
              <AlertTitle>Some fields cannot be changed</AlertTitle>
              <Typography variant="body2">{lockedNotice(baseline.status)}</Typography>
            </Alert>
          )}

          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <EventFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            locked={locked}
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
