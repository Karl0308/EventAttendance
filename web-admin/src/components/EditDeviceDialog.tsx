// The edit-device form: the same controls and the same rules as the register form, filled from the
// device being edited.
//
// `PUT /devices/{id}` is a **full replacement** of the device's own fields, which is why the draft is
// built from the whole `Device` rather than from the boxes the user is likely to touch — a field this
// form cannot reproduce is a field it would blank on save.
//
// It replaces no key. `DeviceService.UpdateAsync` deliberately leaves every `ApiKey*` column alone, so
// nothing here can mint, rotate or burn a credential: those are three separate, differently-worded
// actions on the page. Clearing `Active` does stop the device authenticating, which is a consequence
// worth saying out loud and `DeviceFormFields` says it.

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
  NO_DEVICE_ERRORS,
  VALIDATED_DEVICE_FIELDS,
  deviceFieldIdsFor,
  draftFromDevice,
  validateDevice,
} from "../deviceDraft";
import type { DeviceDraft, DeviceFieldErrors, ValidatedDeviceField } from "../deviceDraft";
import type { Device, DeviceWriteRequest } from "../types";
import { DeviceFormFields } from "./DeviceFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = deviceFieldIdsFor("edit-device");
const DIALOG_TITLE_ID = "edit-device-dialog-title";

const HEADING_NOT_SAVED = "The change was not saved";
const HEADING_MAYBE_SAVED = "The change may have been saved";

/**
 * `PUT` is idempotent, so a second press cannot produce a second anything — but `advise()` withholds
 * the one-click resend anyway, because `shape` is a proxy for idempotency and is exact only for POST.
 * The sentence stays honest about what is actually unknown rather than borrowing the create form's
 * duplicate warning, which would be false here.
 */
const RESEND_WITHHELD =
  "Save is disabled because this build cannot tell whether the change was applied. Close this and " +
  "check the row in the list — it is re-read after every failed attempt.";

interface EditDeviceDialogProps {
  /** The device being edited. The draft is filled from it once, on mount. */
  device: Device;
  onClose: () => void;
  onSubmit: (request: DeviceWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

/**
 * Mounted only while open — see `Devices.tsx` — so every open starts from a draft freshly filled from
 * the device, with no leftovers from the last one edited.
 */
export default function EditDeviceDialog({
  device,
  onClose,
  onSubmit,
  running,
  failure,
}: EditDeviceDialogProps) {
  const [draft, setDraft] = useState<DeviceDraft>(() => draftFromDevice(device));
  const [touched, setTouched] = useState<ReadonlySet<ValidatedDeviceField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateDevice(draft);
  const errors: DeviceFieldErrors = checked.ok ? NO_DEVICE_ERRORS : checked.errors;

  const errorFor = (field: ValidatedDeviceField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedDeviceField) =>
    setTouched((current) => new Set(current).add(field));

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateDevice(draft);
    if (!now.ok) {
      const firstInvalid = VALIDATED_DEVICE_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Edit “{device.name}”</DialogTitle>

        <DialogContent>
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <DeviceFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
            // No key is issued by an edit, so the note promising one would be a promise this form does
            // not keep.
            issuesKey={false}
          />
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : "Save device"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
