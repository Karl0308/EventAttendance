// The one-shot reveal. This is the only screen in the application that ever renders a device key, and
// the only chance anybody gets to read it.
//
// The constraint it is built around is not a preference: `DeviceKeyIssuedDto.apiKey` is the single
// carrier of a plaintext token, returned by the 201 from `POST /devices` and the 200 from
// `POST /devices/{id}/regenerate-key` and by nothing else. The server stores `SHA-256(secret)`, so
// "show it again" has no implementation rather than a refused one. If this dialog is dismissed before
// the token is somewhere else, the only remedy is rotating — which, on a device already in service,
// means the reader is offline until someone walks to it with the replacement.
//
// So three things are deliberate and none of them is decoration:
//
//   - **It does not close by accident.** Escape is disabled and the backdrop is inert. Every other
//     dialog in this app closes on both, and that is right for a form whose worst outcome is retyping
//     it; here the worst outcome is a credential nobody has. The way out is one clearly labelled
//     button that says what closing means.
//   - **The token is selectable text in a real input**, not a `<code>` block. Focus lands on it and
//     selects the whole value, so Ctrl+A/Ctrl+C works, a screen reader can be walked through it
//     character by character, and a browser with no clipboard permission still has a path.
//   - **Copy reports its own failure.** `navigator.clipboard` is absent over plain HTTP and can be
//     refused by permission policy; a Copy button that quietly did nothing on a value that will never
//     be shown again is the worst button in the codebase.
//
// It is NOT given the device's own edit affordances, and nothing here is a form. The dialog exists to
// transfer one string out of the browser and then to be dismissed on purpose.

import { useEffect, useRef, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";
import type { DeviceKeyIssued } from "../types";

const DIALOG_TITLE_ID = "device-key-dialog-title";
const DIALOG_BODY_ID = "device-key-dialog-body";
const TOKEN_FIELD_ID = "device-key-token";

/**
 * What the operator has to be told, in the order they need it: that this is the only showing, what to
 * do about it, and what it costs if they get it wrong. Constants rather than inline JSX so the
 * sentences are edited in one place and read as the specification they are.
 */
const ONLY_SHOWING =
  "This is the only time this key is shown. It is not stored anywhere it can be read back — the " +
  "server keeps only a hash of it — so there is no screen, no export and no support request that can " +
  "produce it again.";

const WHAT_TO_DO =
  "Copy it into the device now, or into wherever your credentials are kept. Treat it like a password: " +
  "it lets whatever holds it record attendance as this device.";

const IF_YOU_LOSE_IT =
  "If it is lost, the device cannot be recovered — only replaced. Issuing a new key is a hard cut, so " +
  "the reader stops working the moment the replacement is minted and stays down until someone enters " +
  "the new token on the device itself.";

/** Said on the button, so "close" is never the shortest way out of this dialog by accident. */
const ACKNOWLEDGE = "I have saved the key — close";

const COPY_SUCCESS = "The key was copied to the clipboard.";

/**
 * Said when the browser has no clipboard to write to — plain HTTP, or a permission policy that
 * withholds it. It names the way out rather than merely reporting the failure, because the value is
 * still on screen and still selectable.
 */
const COPY_UNAVAILABLE =
  "This browser did not allow copying to the clipboard. The key is still in the box above — select " +
  "it and copy it manually before closing this dialog.";

/** How the copy attempt went. `undefined` is "not tried", which is not the same as "failed". */
type CopyResult = { ok: true } | { ok: false; message: string };

/** What the title says when the reply did not carry a readable name. See `IssuedKeyDevice`. */
const UNNAMED_DEVICE = "this device";

interface DeviceKeyDialogProps {
  /** The reply that carried the token, and the device it belongs to. */
  issued: DeviceKeyIssued;
  /**
   * Whether this key replaced a working one — true after a rotation, false after a registration.
   * Changes what the dialog says happened, not what it warns about: on a rotation the previous token
   * has *already* stopped working, and the operator is now holding a reader that is offline until this
   * value reaches it.
   */
  replaced: boolean;
  /** Dismiss. Reachable only from the acknowledge button — not from Escape, not from the backdrop. */
  onClose: () => void;
}

export default function DeviceKeyDialog({ issued, replaced, onClose }: DeviceKeyDialogProps) {
  const [copied, setCopied] = useState<CopyResult | undefined>(undefined);
  // A textarea, because the field is `multiline` — 85 characters on one line is a horizontal scroll
  // through a value that has to be read, not scrolled past.
  const token = useRef<HTMLTextAreaElement>(null);

  // Focus the token and select it, rather than focusing the acknowledge button. The button is the one
  // control that throws the key away, and opening with it under the user's finger means an Enter still
  // held from the press that got here dismisses the dialog before it has been read. Selecting the
  // value also makes Ctrl+C the immediate next keystroke for anyone who does not use the Copy button.
  //
  // Keyed on the token rather than on mount, and `copied` is cleared with it. The page is supposed to
  // refuse a second issue while a reveal is unacknowledged, so this component should never see a second
  // token — but "should never" is the caller's invariant and this is the file whose entire job is that
  // nobody is told a false thing about a credential. If a token is ever replaced under it, a stale
  // “The key was copied to the clipboard” would be asserting that about a value nobody has copied.
  useEffect(() => {
    setCopied(undefined);
    const field = token.current;
    if (field === null) return;
    field.focus();
    field.select();
  }, [issued.apiKey]);

  const copy = () => {
    // Widened on the way in rather than tested in place: `navigator.clipboard` is typed non-nullable,
    // so comparing it to `undefined` directly is a compile error — while at runtime it is genuinely
    // absent over plain HTTP and can be withheld by permission policy. The annotation is what lets the
    // check that matters be written at all.
    const clipboard: Clipboard | undefined = navigator.clipboard;
    if (clipboard === undefined || typeof clipboard.writeText !== "function") {
      setCopied({ ok: false, message: COPY_UNAVAILABLE });
      return;
    }

    // Not a floating promise and not a swallow: both arms end in something the user can read. The
    // browser's own reason is carried through rather than replaced, because "Write permission denied"
    // and "Document is not focused" send someone to different places.
    void clipboard.writeText(issued.apiKey).then(
      () => setCopied({ ok: true }),
      (cause: unknown) =>
        setCopied({
          ok: false,
          message: `${COPY_UNAVAILABLE} (${cause instanceof Error ? cause.message : "no reason given"})`,
        }),
    );
  };

  return (
    <Dialog
      open
      // No `onClose`, and `disableEscapeKeyDown` with it. This is the one dialog in the app that does
      // not close on Escape or on a backdrop click, because both are things a user does without
      // deciding to — and here the undo for that is rotating a credential. The acknowledge button
      // below is always visible, always reachable by Tab, and says what it does.
      disableEscapeKeyDown
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      aria-describedby={DIALOG_BODY_ID}
    >
      <DialogTitle id={DIALOG_TITLE_ID}>
        {replaced ? "New key for" : "Key for"}{" "}
        {issued.device.name === undefined ? UNNAMED_DEVICE : `“${issued.device.name}”`}
      </DialogTitle>

      <DialogContent>
        {/* `role="alert"` so the one-showing warning is announced rather than merely painted — the
            dialog is announced by its title and description, and this sentence outranks both. */}
        <Alert severity="warning" role="alert" sx={{ mb: 2 }}>
          <Typography variant="body2" fontWeight={700}>
            {ONLY_SHOWING}
          </Typography>
        </Alert>

        <DialogContentText id={DIALOG_BODY_ID}>
          {replaced
            ? "The previous key stopped working the moment this one was issued — there is no overlap " +
              "period. This device cannot record attendance until the token below is entered on it."
            : "The device is registered. It cannot record attendance until the token below is entered " +
              "on it."}
        </DialogContentText>

        {/* A caveat on the *identity*, never on the token. The reply carried a key this build could
            not fully describe the device for; the key is still the key, and withholding the reveal
            over a display field would destroy the one copy of it. `status` rather than `alert`: the
            one-showing warning above outranks this and must not be interrupted by it. */}
        {issued.deviceDrift !== undefined && (
          <Alert severity="info" role="status" sx={{ mt: 2 }}>
            <Typography variant="body2">{issued.deviceDrift}</Typography>
          </Alert>
        )}

        <TextField
          id={TOKEN_FIELD_ID}
          inputRef={token}
          label="Device key"
          value={issued.apiKey}
          // A field labelled "Device key" holding an 85-character secret is exactly what a password
          // manager offers to save, and this value must not end up in one: it belongs on the reader,
          // not in the browser profile of whoever registered it.
          autoComplete="off"
          // Read-only rather than disabled: a disabled input is out of the tab order and its text
          // cannot be selected, which would leave a keyboard user with no way to reach the one value
          // this dialog exists to hand over.
          slotProps={{
            input: { readOnly: true },
            htmlInput: { spellCheck: false },
            formHelperText: { id: `${TOKEN_FIELD_ID}-help` },
          }}
          // Re-selects on every focus, so tabbing back to it after a failed copy does not require
          // dragging across 85 wrapped characters.
          onFocus={(e) => e.target.select()}
          multiline
          fullWidth
          sx={{
            mt: 2,
            // Monospace and break-anywhere: the token is hex, so `l`/`1` and `0`/`O` have to be
            // distinguishable for anyone reading it aloud or typing it into a handset by hand.
            "& textarea": { fontFamily: "monospace", wordBreak: "break-all" },
          }}
          helperText="Case-sensitive, and copied whole — the eams_dk_ prefix is part of the key."
        />

        <Stack direction="row" spacing={2} alignItems="center" sx={{ mt: 1 }}>
          <Button variant="outlined" startIcon={<ContentCopyIcon />} onClick={copy}>
            Copy key
          </Button>
          {/* Mounted whether or not a copy has been tried, so the live region exists in the DOM before
              anything is put into it — a region inserted and populated in the same commit is announced
              unreliably. */}
          <Box
            role={copied !== undefined && !copied.ok ? "alert" : "status"}
            sx={{ flexGrow: 1, minHeight: 24 }}
          >
            {copied?.ok === true && (
              <Typography variant="body2" color="success.main">
                {COPY_SUCCESS}
              </Typography>
            )}
            {copied !== undefined && !copied.ok && (
              <Typography variant="body2" color="error.main">
                {copied.message}
              </Typography>
            )}
          </Box>
        </Stack>

        <DialogContentText sx={{ mt: 2 }}>{WHAT_TO_DO}</DialogContentText>
        <DialogContentText sx={{ mt: 1 }}>{IF_YOU_LOSE_IT}</DialogContentText>

        <DialogContentText sx={{ mt: 2 }} variant="body2">
          The public half of this key is{" "}
          <Box component="code" sx={{ fontFamily: "monospace" }}>
            {issued.device.apiKeyId ?? "not reported"}
          </Box>
          . That part is safe to write down and to quote: it is what identifies this credential in a
          log line without being one.
        </DialogContentText>
      </DialogContent>

      <DialogActions>
        {/* The only way out, and it says what it means. Not `autoFocus` — see the effect above. */}
        <Button onClick={onClose} variant="contained">
          {ACKNOWLEDGE}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
