// The confirmation that stands between a click and a rotation — and, more to the point, the place where
// the thing that makes rotation expensive is said *before* it happens.
//
// `POST /devices/{id}/regenerate-key` is a hard cut. The new key overwrites the old one in the same
// row, there is no second key column, and therefore no overlap window: the moment this returns, the
// device in the gym is refused. That is a deliberate server-side design choice, not a gap — but it
// means the operator is choosing to take a reader offline, and a confirmation that only said "are you
// sure?" would let them find that out from a queue of students who cannot tap in.
//
// Three beliefs an admin can hold here, each of which this dialog exists to correct:
//
//   - "The old key keeps working until I enter the new one." It does not. It stops on the round trip.
//   - "I can rotate now and read the key later." They cannot. The token is shown once, on the next
//     screen, and there is no endpoint that produces it again.
//   - "This is how I take a device out of service." It is not — that is the Active checkbox on the edit
//     form, which keeps the key. Rotating a device you meant to switch off leaves it live under a
//     credential nobody has entered.

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
import { keyStanding } from "../deviceDraft";
import type { KeyStandingKind } from "../deviceDraft";
import type { Device } from "../types";

const DIALOG_TITLE_ID = "regenerate-device-key-dialog-title";
const DIALOG_BODY_ID = "regenerate-device-key-dialog-body";

const HEADING_NOT_ISSUED = "No new key was issued";
const HEADING_MAYBE_ISSUED = "A new key may have been issued";

/**
 * The worst failure on this surface, and the reason the sentence is this specific.
 *
 * If the rotation was applied and the reply was lost, the server holds the hash of a token **nobody has
 * ever seen** — the one copy went into a response this build could not read. The device is then offline
 * with no recoverable credential, and the only way out is rotating again, which is exactly the button
 * being withheld. Saying "check the list" would be useless: the list cannot show a key. So this points
 * at the fact that actually decides what to do next — the issued-at timestamp on the row.
 */
const RESEND_WITHHELD =
  "Issuing is disabled because this build cannot tell whether a new key was minted. If it was, the " +
  "token was in the reply that was lost and nobody has it — the device is already offline. Close this " +
  "and check the row's “key issued” time: if it just moved, rotate once more deliberately and keep " +
  "the token this time.";

/**
 * What to say when there is no working key to cut off — one entry per non-`active` standing, because
 * "issuing puts it back in service" is true of three of them and **false of one**: a switched-off
 * device comes back by ticking Active, and rotating it leaves it exactly as off as it was, now with a
 * token someone has to walk to the reader with.
 */
const NOTHING_TO_CUT_OFF: Record<Exclude<KeyStandingKind, "active">, string> = {
  "never-issued":
    "This device has never been issued a key, so there is no working credential to cut off. " +
    "Issuing one is what puts it into service.",
  revoked:
    "This device's key was revoked, so there is no working credential to cut off. Issuing a new key " +
    "clears the revocation and is the only way back.",
  "device-inactive":
    "This device is marked inactive, so its key is not accepted and there is nothing to cut off. " +
    "Rotating will not switch it back on: close this, edit the device and tick Active, which keeps " +
    "the key it already has and needs nothing typed into the reader.",
  indeterminate:
    "This device is switched on and its key is not revoked, yet the server still refuses it — the " +
    "stored credential is incomplete, so there is nothing to cut off. Issuing a new key is the only " +
    "remedy.",
};

/**
 * Appended to the two entries above that are chosen *before* the active flag is looked at.
 *
 * `keyStanding` reports `revoked` and `never-issued` ahead of `device-inactive`, which is right — those
 * are the first thing to fix — but it means either can describe a device that is *also* switched off,
 * and for that device "issuing puts it back in service" is false. Rotation clears the revocation and
 * writes a hash; `IsActive` is untouched, so the reader goes on refusing it. Without this clause the
 * operator spends a one-shot token and walks to the door for nothing.
 *
 * The other two entries need no clause: `device-inactive` already names the checkbox, and
 * `indeterminate` is only reached on a device that is switched on.
 */
const ALSO_SWITCHED_OFF =
  " This device is also switched off, so issuing alone will not put it back in service — edit it and " +
  "tick Active as well.";

interface RegenerateDeviceKeyDialogProps {
  device: Device;
  onClose: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function RegenerateDeviceKeyDialog({
  device,
  onClose,
  onConfirm,
  running,
  failure,
}: RegenerateDeviceKeyDialogProps) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  /**
   * Whether there is a working credential to lose. A device that has never been issued a key, or whose
   * key is revoked, has nothing to cut off — so the hard-cut warning would be a warning about nothing,
   * and on a revoked device rotating is the *remedy* rather than the risk.
   *
   * Read from `keyStanding` rather than re-derived here. The hand-rolled version was
   * `apiKeyId !== undefined && apiKeyRevokedAt === undefined`, which omits the `isActive` fold — so a
   * switched-off device showed a "Switched off" chip in the grid and was warned here that its working
   * key would stop working. One question, one answer, one function.
   */
  const standing = keyStanding(device);
  const replacesWorkingKey = standing.kind === "active";

  return (
    // `aria-describedby` as well as `aria-labelledby`: on a confirmation the consequences *are* the
    // content, and a dialog announced by its title alone reads as "Issue a new key? button Cancel
    // button Issue" — with the sentences that would change the answer never spoken.
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={DIALOG_BODY_ID}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>Issue a new key for “{device.name}”?</DialogTitle>

      <DialogContent>
        <DialogContentText id={DIALOG_BODY_ID}>
          {/* The direct comparison rather than `replacesWorkingKey` is what narrows the kind for the
              lookup below — the same test, written where TypeScript can use it. */}
          {standing.kind === "active" ? (
            <>
              The key this device is using now <strong>stops working immediately</strong>. There is no
              overlap period: it is refused from the moment the new key is minted, and the device
              cannot record attendance again until someone enters the new token on it in person.
            </>
          ) : (
            <>
              {NOTHING_TO_CUT_OFF[standing.kind]}
              {/* `device-inactive` is excluded because its entry already sends them to the checkbox;
                  `indeterminate` cannot be switched off, so it never reaches this. */}
              {standing.deviceOff && standing.kind !== "device-inactive" ? ALSO_SWITCHED_OFF : ""}
            </>
          )}
        </DialogContentText>

        <DialogContentText sx={{ mt: 2 }}>
          The new token is shown <strong>once</strong>, on the next screen, and cannot be retrieved
          afterwards — the server keeps only a hash of it. Have somewhere to put it before you press
          this.
        </DialogContentText>

        {replacesWorkingKey && (
          <DialogContentText sx={{ mt: 2 }}>
            If you only want this reader out of service for a while, this is the wrong button: edit it
            and clear <strong>Active</strong> instead, which keeps the key so switching it back on
            needs nothing typed into the device.
          </DialogContentText>
        )}

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_ISSUED}
            mayHaveApplied={HEADING_MAYBE_ISSUED}
            resendWithheld={RESEND_WITHHELD}
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Cancel takes focus, not the confirm. The dialog opens with the disruptive control one Tab
            away rather than under a keyboard user's finger, so an Enter pressed out of habit — or still
            held from the button that opened this — dismisses rather than rotates. */}
        <Button onClick={onClose} autoFocus>
          Cancel
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          // `warning` rather than `error`: rotating is not destructive in the way revoking is — it ends
          // with the device working again — but it does take it down in between, so it must not look
          // like an ordinary confirm either.
          color="warning"
          disabled={running || resendUnsafe}
        >
          {/* Names what it acts on: a screen reader reads a dialog's buttons out of context. */}
          {running ? "Issuing…" : "Issue a new key"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
