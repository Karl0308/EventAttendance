// The confirmation that stands between a click and a burned credential.
//
// Revoking is the one action on this page that destroys a working thing and gives nothing back. It is
// deliberately *not* the same statement as deactivating the device (that is the Active checkbox, which
// keeps the key) and deliberately not the same as rotating (which ends with the device working again).
// The reason it exists at all is that the published mobile contract distinguishes `401` — key not
// recognised — from `403` — key turned off, so a handset knows whether to re-enrol; without this route
// that 403 is unreachable.
//
// Two beliefs an admin can hold here, and both cost real time:
//
//   - "I lost the token, so I should revoke it." Almost never. Revoking leaves the device with no
//     credential at all; **rotating** is what a lost token needs, and it hands back a replacement in
//     the same press. Revoke is for a token that is in the wrong hands, or a device being retired.
//   - "I can undo this." Not as such. There is no un-revoke: the only way back is issuing a new key,
//     which means the token on the device is dead either way and someone has to visit it.

import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import { WriteFailureAlert } from "./WriteFailureAlert";
import type { Device } from "../types";

const DIALOG_TITLE_ID = "revoke-device-key-dialog-title";
const DIALOG_BODY_ID = "revoke-device-key-dialog-body";

const HEADING_NOT_REVOKED = "The key was not revoked";
const HEADING_MAYBE_REVOKED = "The key may have been revoked";

/**
 * Withheld for the same reason and with the same caveat as the student delete's: this endpoint *is*
 * idempotent — revoking twice, or revoking a device that never held a key, is a 200, because the
 * postcondition holds either way — but `advise()` withholds the one-click resend because `shape` is a
 * proxy for idempotency and is exact only for POST. It errs safe, and the sentence stays honest about
 * what is actually unknown. Unlike a create, checking is cheap: the row says so.
 */
const RESEND_WITHHELD =
  "Revoke is disabled because this build cannot tell whether the key was revoked. Close this and look " +
  "at the row — it is re-read after every failed attempt, and it says “Key revoked” if it was. " +
  "Revoking is safe to repeat if it was not.";

interface RevokeDeviceKeyDialogProps {
  device: Device;
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function RevokeDeviceKeyDialog({
  device,
  onClose,
  onConfirm,
  running,
  failure,
}: RevokeDeviceKeyDialogProps) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={DIALOG_BODY_ID}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>Revoke the key for “{device.name}”?</DialogTitle>

      <DialogContent>
        <DialogContentText id={DIALOG_BODY_ID}>
          This device <strong>stops authenticating immediately</strong> and is left with no credential
          at all. Every request it makes is refused from the moment this returns, and it cannot record
          attendance again until a new key is issued and entered on the device in person.
        </DialogContentText>

        <DialogContentText sx={{ mt: 2 }}>
          <strong>If you have simply lost the token, this is the wrong button.</strong> Use{" "}
          <strong>Issue a new key</strong> instead: it burns the old one and hands you a working
          replacement in the same press, where this leaves the device dead until you come back and
          rotate it anyway.
        </DialogContentText>

        <DialogContentText sx={{ mt: 2 }}>
          Revoke is the right button when the token has been exposed or the device is being retired.
          There is no way to un-revoke — issuing a new key is the only route back, so the token on the
          device is finished either way.
        </DialogContentText>

        {/* Said only where it could be true, rather than as a general disclaimer nobody reads. */}
        {device.apiKeyLastUsedAt !== undefined && (
          <DialogContentText sx={{ mt: 2 }}>
            This key was last accepted at{" "}
            {new Date(device.apiKeyLastUsedAt).toLocaleString()} — recently enough that something may
            be relying on it right now. That timestamp is written throttled, so it lags real use.
          </DialogContentText>
        )}

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_REVOKED}
            mayHaveApplied={HEADING_MAYBE_REVOKED}
            resendWithheld={RESEND_WITHHELD}
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Cancel takes focus, not Revoke — the destructive control is one Tab away rather than under a
            keyboard user's finger. */}
        <Button onClick={onClose} autoFocus>
          Cancel
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          color="error"
          disabled={running || resendUnsafe}
        >
          {running ? "Revoking…" : "Revoke key"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
