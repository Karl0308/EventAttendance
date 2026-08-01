// The register-device form: an empty draft, the shared rules, and the failure the page hands back. The
// write itself is NOT here — `Devices.tsx` owns it, for the reason `NewStudentDialog` records: a dialog
// that owns its own write has to block its own dismissal for as long as the request runs, and that lock
// reliably traps a keyboard user without closing the hole it was built for.
//
// There is one thing about this form that is not true of the student one: submitting it **mints a
// credential**, and the reply carrying that credential is the only copy of it that will ever exist. So
// the page does not merely close this dialog on success — it replaces it with `DeviceKeyDialog`. The
// success path here therefore ends by handing the outcome up, and this component never sees the token.

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
  EMPTY_DEVICE_DRAFT,
  NO_DEVICE_ERRORS,
  VALIDATED_DEVICE_FIELDS,
  deviceFieldIdsFor,
  validateDevice,
} from "../deviceDraft";
import type { DeviceDraft, DeviceFieldErrors, ValidatedDeviceField } from "../deviceDraft";
import type { DeviceWriteRequest } from "../types";
import { DeviceFormFields } from "./DeviceFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = deviceFieldIdsFor("new-device");
const DIALOG_TITLE_ID = "new-device-dialog-title";

/** The server decided and wrote nothing: a §4.10 field refusal, or a 409 with no school to file it under. */
const HEADING_NOT_REGISTERED = "The device was not registered";

/** The request went out and this build cannot say what became of it. */
const HEADING_MAYBE_REGISTERED = "The device may have been registered";

/**
 * Shown whenever Register is disabled by the failure rather than by the request in flight.
 *
 * `POST /devices` carries no idempotency key, so a second press is how a school ends up with two rows
 * for one door — and here the duplicate is worse than a duplicate student, because the second row
 * carries a *second live credential* that nothing on this screen would identify as surplus. The way out
 * is the list, which has already been re-read.
 */
const RESEND_WITHHELD =
  "Register is disabled because pressing it again could register the same device twice — and a second " +
  "device means a second live key that nobody is holding. Close this dialog and check the list, which " +
  "is re-read after every failed attempt, before trying again.";

interface NewDeviceDialogProps {
  /** Dismiss without registering: Cancel, Escape, backdrop. Always available, in flight or not. */
  onClose: () => void;
  /** Send this draft. Returns nothing — the page owns the mutation, so the outcome is the page's. */
  onSubmit: (request: DeviceWriteRequest) => void;
  /** The page's register is in flight. */
  running: boolean;
  /**
   * How the last register failed, or `undefined` for "no failure to show". Wrapped in an object rather
   * than passed as a bare `unknown`, because `unknown` includes `undefined`.
   */
  failure: { error: unknown } | undefined;
}

/** Mounted only while open — see `Devices.tsx` — so every open starts from a fresh empty draft. */
export default function NewDeviceDialog({
  onClose,
  onSubmit,
  running,
  failure,
}: NewDeviceDialogProps) {
  const [draft, setDraft] = useState<DeviceDraft>(EMPTY_DEVICE_DRAFT);
  const [touched, setTouched] = useState<ReadonlySet<ValidatedDeviceField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateDevice(draft);
  const errors: DeviceFieldErrors = checked.ok ? NO_DEVICE_ERRORS : checked.errors;

  /**
   * Errors are computed on every keystroke and *shown* only once the user has left the field or pressed
   * Register — a form that shouts before the user has finished a word is one people learn to ignore.
   */
  const errorFor = (field: ValidatedDeviceField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedDeviceField) =>
    setTouched((current) => new Set(current).add(field));

  /** `isResendUnsafe` is `advise().retryable !== "safe"` — never a negation; see that function. */
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    // Re-validated here rather than reusing `checked` from render: they agree today, and relying on
    // that is relying on this handler never being called from a closure one render behind.
    const now = validateDevice(draft);
    if (!now.ok) {
      // Focus follows the refusal (WCAG 3.3.1): the submit button is at the bottom of the dialog, so a
      // keyboard user told "check the fields" and left standing on it would have to Shift+Tab past
      // everything to reach the problem.
      const firstInvalid = VALIDATED_DEVICE_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    // `onClose` unconditionally: Escape and the backdrop close this whatever the write is doing. The
    // page owns the outcome and is still here to receive it — including the issued key, which is why
    // dismissing this dialog mid-flight does NOT lose the token: `DeviceKeyDialog` is opened by the
    // page, not from inside here.
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      {/* A real <form>, so Enter submits from any field. `noValidate` because `required` is here to be
          announced, not enforced by a native bubble no screen reader reads reliably — one set of
          rules, mirroring the server's. */}
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Register a device</DialogTitle>

        <DialogContent>
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_REGISTERED}
              mayHaveApplied={HEADING_MAYBE_REGISTERED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <DeviceFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            issuesKey
          />
        </DialogContent>

        <DialogActions>
          {/* Never disabled — blocking Cancel while a write ran was the fifteen-second trap. */}
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            // The courtesy; the guard that actually stops a double register is the in-flight ref inside
            // `useApiMutation`, because this attribute only lands on the next commit.
            //
            // Deliberately NOT disabled for a form with errors in it: a button that greys out without
            // saying why leaves the user hunting.
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Registering…" : "Register device"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
