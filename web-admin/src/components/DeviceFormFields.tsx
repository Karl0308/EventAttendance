// The four controls a device is described by, rendered once for both the form that registers one and
// the form that edits one.
//
// The two forms differ in their title, their submit button and what they say before the fields — none
// of which is a control. `StudentFormFields`' reasoning applies unchanged: copying the controls into a
// second dialog would put every label and every helper sentence in two places, so a limit corrected in
// one form would go on being wrong in the other.

import { Checkbox, FormControlLabel, MenuItem, Stack, TextField, Typography } from "@mui/material";
import { DEVICE_TYPES } from "../types";
import {
  DEVICE_NAME_MAX_LENGTH,
  DEVICE_TYPE_LABELS,
  READER_MODEL_MAX_LENGTH,
} from "../deviceDraft";
import type { DeviceDraft, DeviceDraftField, ValidatedDeviceField } from "../deviceDraft";

/**
 * Said on the Active checkbox, and it is the sentence that stops the box being read as a revoke.
 *
 * Clearing it makes `hasActiveKey` false — the device stops authenticating — while leaving every
 * `ApiKey*` column untouched, so ticking it again restores the *same* key. An operator who believed
 * this burned the credential would rotate a device that only needed switching back on, and take it
 * offline for as long as it takes someone to walk to the door with the new token.
 */
const IS_ACTIVE_HELP =
  "An inactive device stops being accepted immediately, but its key is kept: ticking this again " +
  "restores the same token, with nothing to re-enter on the device. Use this for a reader that is " +
  "out of service — revoke the key instead if the token itself has been lost or exposed.";

/** The key is minted by the server, so the form has to say where it comes from rather than ask. */
const KEY_NOTE_REGISTER =
  "A key is issued automatically when the device is registered, and the token is shown once on the " +
  "next screen. It cannot be retrieved afterwards.";

interface DeviceFormFieldsProps {
  draft: DeviceDraft;
  /** A partial draft to merge. The owner holds the state; these controls only describe changes. */
  onChange: (patch: Partial<DeviceDraft>) => void;
  /** Field ids, from `deviceFieldIdsFor`. MUI builds `<label for>` and `aria-describedby` from them. */
  ids: Record<DeviceDraftField, string>;
  /** The error to show for a field, or `undefined` — the owner decides *when* an error is shown. */
  errorFor: (field: ValidatedDeviceField) => string | undefined;
  /** The user has left this field at least once. */
  onBlur: (field: ValidatedDeviceField) => void;
  /**
   * Whether this form will mint a key when it is submitted — true on register, false on edit, because
   * `PUT /devices/{id}` deliberately touches no key column. Decides whether the note above is shown at
   * all: on an edit form it would promise a token that is not coming.
   */
  issuesKey: boolean;
}

export function DeviceFormFields({
  draft,
  onChange,
  ids,
  errorFor,
  onBlur,
  issuesKey,
}: DeviceFormFieldsProps) {
  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField
        id={ids.name}
        label="Device name"
        value={draft.name}
        onChange={(e) => onChange({ name: e.target.value })}
        onBlur={() => onBlur("name")}
        error={errorFor("name") !== undefined}
        helperText={
          errorFor("name") ??
          `Required. What someone standing next to it would call it — “Gym main door”. Up to ${DEVICE_NAME_MAX_LENGTH} characters.`
        }
        required
        // The dialog's first focusable control, so opening it lands on the thing to type in.
        autoFocus
        // Every box here describes a piece of equipment, never the person at the keyboard, so browser
        // autofill has nothing correct to offer.
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.deviceType}
        label="Device type"
        select
        value={draft.deviceType}
        // Narrowed rather than cast: the value can only be one of the options rendered below, and
        // finding it in the same list the options are built from is what makes that true at runtime as
        // well as in the type. A value that is not in the set leaves the draft unchanged, which is the
        // honest answer to an event that cannot happen.
        onChange={(e) => {
          const chosen = DEVICE_TYPES.find((type) => type === e.target.value);
          if (chosen !== undefined) onChange({ deviceType: chosen });
        }}
        helperText="What kind of reader this is. It is recorded on the device; it does not change how taps are captured."
        fullWidth
      >
        {/* Built from the set rather than from three hand-typed items, so the picker cannot drift from
            what the server accepts. */}
        {DEVICE_TYPES.map((type) => (
          <MenuItem key={type} value={type}>
            {DEVICE_TYPE_LABELS[type]}
          </MenuItem>
        ))}
      </TextField>

      <TextField
        id={ids.readerModel}
        label="Reader model"
        value={draft.readerModel}
        onChange={(e) => onChange({ readerModel: e.target.value })}
        onBlur={() => onBlur("readerModel")}
        error={errorFor("readerModel") !== undefined}
        helperText={
          errorFor("readerModel") ??
          `Optional. The hardware, for whoever has to service it. Up to ${READER_MODEL_MAX_LENGTH} characters.`
        }
        autoComplete="off"
        fullWidth
      />

      <FormControlLabel
        control={
          <Checkbox
            id={ids.isActive}
            checked={draft.isActive}
            onChange={(e) => onChange({ isActive: e.target.checked })}
          />
        }
        label="Active"
      />
      {/* Beside the checkbox rather than inside its label: MUI has no `helperText` on
          `FormControlLabel`, and folding three sentences into the label would have a screen reader
          read the whole paragraph every time focus lands on the box. */}
      <Typography variant="body2" color="text.secondary" sx={{ mt: -1 }}>
        {IS_ACTIVE_HELP}
      </Typography>

      {issuesKey && (
        <Typography variant="body2" color="text.secondary">
          {KEY_NOTE_REGISTER}
        </Typography>
      )}
    </Stack>
  );
}
